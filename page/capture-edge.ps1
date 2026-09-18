# 抓取 Edge 窗口截图，用来观察页面当前的真实状态。
# 用法: powershell -File capture-edge.ps1 [-Match "极简"] [-Out shot.png]

param(
    [string]$Match = "",
    [string]$Out = "edge-shot.png"
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public class Cap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

  public static List<string> Windows() {
    var list = new List<string>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (!IsWindowVisible(h)) return true;
      var t = new StringBuilder(512); GetWindowText(h, t, 512);
      var c = new StringBuilder(256); GetClassName(h, c, 256);
      uint pid; GetWindowThreadProcessId(h, out pid);
      RECT r; GetWindowRect(h, out r);
      list.Add(h.ToInt64() + "\t" + pid + "\t" + c.ToString() + "\t" +
               (r.R-r.L) + "x" + (r.B-r.T) + "\t" + t.ToString());
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@

$wins = [Cap]::Windows() | ForEach-Object {
    $p = $_ -split "`t"
    [pscustomobject]@{ Hwnd = [int64]$p[0]; Pid = [int]$p[1]; Class = $p[2]; Size = $p[3]; Title = $p[4] }
}

Write-Host "=== 所有可见顶层窗口 ==="
$wins | Where-Object { $_.Title -ne "" } | ForEach-Object {
    Write-Host ("  hwnd={0,-10} pid={1,-7} {2,-28} {3,-12} {4}" -f $_.Hwnd, $_.Pid, $_.Class, $_.Size, $_.Title)
}

$target = $null
if ($Match) {
    $target = $wins | Where-Object { $_.Title -like "*$Match*" } | Select-Object -First 1
}
if (-not $target) {
    # 退而求其次：找 msedge 的最大窗口
    $edgePids = (Get-Process msedge -ErrorAction SilentlyContinue).Id
    $target = $wins | Where-Object { $edgePids -contains $_.Pid -and $_.Title -ne "" } |
              Sort-Object { [int]($_.Size -split 'x')[0] } -Descending | Select-Object -First 1
}

if (-not $target) { Write-Host "`n没找到目标窗口"; exit 1 }

Write-Host "`n截图目标: hwnd=$($target.Hwnd) '$($target.Title)' $($target.Size)"
$h = [IntPtr]$target.Hwnd
$r = New-Object Cap+RECT
[void][Cap]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $ht = $r.B - $r.T
if ($w -le 0 -or $ht -le 0) { Write-Host "窗口尺寸异常"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT=2，Chromium 窗口需要这个
[void][Cap]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$full = Join-Path (Get-Location) $Out
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "已保存: $full"
