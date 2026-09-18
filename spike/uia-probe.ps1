# Probe the UI Automation tree of WeChat 4.x.
#
# Goal: find out whether we can read WeChat messages WITHOUT touching the
# protocol, downgrading WeChat, or installing a hook -- using only the
# UI Automation that ships with Windows.
#
# If yes, the bridge can be pure PowerShell (preinstalled on every Windows).
#
# NOTE: ASCII only on purpose. Windows PowerShell 5.1 reads .ps1 as ANSI
# unless the file has a UTF-8 BOM, so non-ASCII would break parsing.
#
# This prints STRUCTURE ONLY (control types, name lengths), not chat content.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
# NOT $AnyCond: PowerShell variable names are case-insensitive, so $AnyCond collides
# with the built-in read-only $AnyCond and silently turns into a Boolean.
$AnyCond = [System.Windows.Automation.Condition]::TrueCondition

function Get-Info($el) {
    try {
        $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        $nm = $el.Current.Name
        $len = 0
        if ($nm) { $len = $nm.Length }
        return [pscustomobject]@{
            Type    = $ct
            NameLen = $len
            HasName = $(if ($len -gt 0) { 'Y' } else { '-' })
            Class   = $el.Current.ClassName
        }
    } catch {
        return [pscustomobject]@{ Type = '?'; NameLen = 0; HasName = '-'; Class = '?' }
    }
}

function Walk($el, $depth, $maxDepth) {
    if ($depth -gt $maxDepth) { return }
    $kids = $null
    try { $kids = $el.FindAll($TS::Children, $AnyCond) } catch { return }
    if (-not $kids -or $kids.Count -eq 0) { return }

    $pad = ' ' * ($depth * 2)
    $i = 0
    foreach ($k in $kids) {
        if ($i -ge 20) {
            Write-Host ($pad + "... " + ($kids.Count - $i) + " more siblings")
            break
        }
        $d = Get-Info $k
        $line = $pad + '[' + $d.Type + '] name=' + $d.HasName + '(len ' + $d.NameLen + ') class=' + $d.Class
        Write-Host $line
        Walk $k ($depth + 1) $maxDepth
        $i++
    }
}

Write-Host "=== locate WeChat top-level window ==="
$root = $AE::RootElement
$all = $root.FindAll($TS::Children, $AnyCond)

$target = $null
foreach ($w in $all) {
    try {
        # NOT $pid: $PID is a built-in read-only automatic variable (current
        # process id). Assigning to it throws, and catch{} would hide that.
        $procId = $w.Current.ProcessId
        $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -match 'Weixin|WeChat') {
            $nm = $w.Current.Name
            $cn = $w.Current.ClassName
            Write-Host ("  window: class='" + $cn + "' procId=" + $procId + " proc=" + $p.ProcessName + " nameLen=" + $nm.Length)
            if (-not $target) { $target = $w }
        }
    } catch {
        Write-Host ("  [err] " + $_.Exception.Message)
    }
}

if (-not $target) {
    Write-Host "  NOT FOUND - WeChat may be minimized to tray, or UIA cannot see it"
    exit 1
}

Write-Host ""
Write-Host "=== UIA tree (depth 6, structure only) ==="
Walk $target 0 6

Write-Host ""
Write-Host "=== control type census ==="
$allDesc = $target.FindAll($TS::Descendants, $AnyCond)
Write-Host ("  total descendants: " + $allDesc.Count)

$stats = @{}
foreach ($e in $allDesc) {
    try {
        $t = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        if (-not $stats.ContainsKey($t)) { $stats[$t] = 0 }
        $stats[$t] = $stats[$t] + 1
    } catch {}
}
$stats.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    Write-Host ("  " + $_.Key.PadRight(16) + " " + $_.Value)
}

Write-Host ""
Write-Host "=== text-bearing elements (count + max name length only) ==="
$textish = 0
$maxLen = 0
foreach ($e in $allDesc) {
    try {
        $t = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        if ($t -eq 'ControlType.Text' -or $t -eq 'ControlType.ListItem') {
            $textish++
            $l = $e.Current.Name.Length
            if ($l -gt $maxLen) { $maxLen = $l }
        }
    } catch {}
}
Write-Host ("  Text/ListItem elements: " + $textish)
Write-Host ("  longest name seen: " + $maxLen + " chars")
