# 验证 bridge 的位置无关性 与 最小化行为。
#
#   A) 把微信窗口挪位置 + 改尺寸，再发送  -> 检验有没有坐标依赖
#   B) 把微信窗口最小化，再发送            -> 检验最小化是否可用
#
# 结束后把窗口还原到原始位置和尺寸。

Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool rp);
}
"@

function Get-WeChatHwnd {
    $p = Get-Process Weixin -ErrorAction SilentlyContinue |
         Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) { return $p.MainWindowHandle }
    return [IntPtr]::Zero
}

$h = Get-WeChatHwnd
if ($h -eq [IntPtr]::Zero) { Write-Host "找不到微信窗口"; exit 1 }

$r0 = New-Object Win+RECT
[void][Win]::GetWindowRect($h, [ref]$r0)
Write-Host ("原始窗口: L={0} T={1} W={2} H={3}  minimized={4}" -f `
    $r0.L, $r0.T, ($r0.R-$r0.L), ($r0.B-$r0.T), [Win]::IsIconic($h))

Write-Host "`n===== A) 挪位置 + 改尺寸后发送 ====="
$newX = 40; $newY = 30; $newW = 900; $newH = 560
[void][Win]::MoveWindow($h, $newX, $newY, $newW, $newH, $true)
Start-Sleep -Milliseconds 400
$r1 = New-Object Win+RECT
[void][Win]::GetWindowRect($h, [ref]$r1)
Write-Host ("  移动后: L={0} T={1} W={2} H={3}" -f $r1.L, $r1.T, ($r1.R-$r1.L), ($r1.B-$r1.T))

python probe.py --send "POS-TEST-1 窗口被挪到 (40,30) 且缩小到 900x560"
Start-Sleep -Seconds 2

Write-Host "`n===== B) 最小化后发送 ====="
[void][Win]::ShowWindow($h, 6)   # SW_MINIMIZE
Start-Sleep -Milliseconds 600
Write-Host ("  最小化状态: minimized={0}" -f [Win]::IsIconic($h))

python probe.py --send "MIN-TEST-1 这条是在窗口最小化状态下发的"
Start-Sleep -Seconds 2
$h2 = Get-WeChatHwnd
Write-Host ("  发送后: minimized={0}  handle={1}" -f [Win]::IsIconic($h2), $h2)

Write-Host "`n===== 截图验收 ====="
python probe.py --shot wx-posmin.png

Write-Host "`n===== 还原窗口 ====="
[void][Win]::ShowWindow($h, 9)   # SW_RESTORE
Start-Sleep -Milliseconds 400
[void][Win]::MoveWindow($h, $r0.L, $r0.T, ($r0.R-$r0.L), ($r0.B-$r0.T), $true)
Start-Sleep -Milliseconds 400
$r2 = New-Object Win+RECT
[void][Win]::GetWindowRect($h, [ref]$r2)
Write-Host ("  已还原: L={0} T={1} W={2} H={3}" -f $r2.L, $r2.T, ($r2.R-$r2.L), ($r2.B-$r2.T))
