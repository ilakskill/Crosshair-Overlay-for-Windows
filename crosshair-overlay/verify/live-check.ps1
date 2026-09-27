# NON-DESTRUCTIVE live acceptance check for the running Crosshair overlay.
# Never stops/starts the process, never writes crosshair.ini. Only sends the toggle hotkey (even number of presses).
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$root = 'C:\Dev\Crosshairs\crosshair-overlay'
$verifyDir = Join-Path $root 'verify'
$iniPath = Join-Path $root 'crosshair.ini'
$exePath = Join-Path $root 'Crosshair.exe'
$pngPath = Join-Path $verifyDir 'live-center.png'

Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(System.Drawing.Point p);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint flags);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
'@ -ReferencedAssemblies System.Drawing

$script:results = @()
function Report([string]$name, [bool]$pass, [string]$detail) {
    $tag = if ($pass) { 'PASS' } else { 'FAIL' }
    Write-Output ("[{0}] {1}: {2}" -f $tag, $name, $detail)
    $script:results += [pscustomobject]@{ name = $name; passed = $pass }
}
function Describe-Window([IntPtr]$h) {
    if ($h -eq [IntPtr]::Zero) { return 'NULL' }
    $t = New-Object System.Text.StringBuilder 256
    $c = New-Object System.Text.StringBuilder 256
    [void][Win32.Native]::GetWindowText($h, $t, 256)
    [void][Win32.Native]::GetClassName($h, $c, 256)
    $procId = 0
    [void][Win32.Native]::GetWindowThreadProcessId($h, [ref]$procId)
    $pname = try { (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { '?' }
    return ('0x{0:X} class="{1}" text="{2}" proc={3}' -f $h.ToInt64(), $c.ToString(), $t.ToString(), $pname)
}

# ---------- Step 1: read ini (read-only) ----------
$hashStart = (Get-FileHash -Algorithm SHA256 $iniPath).Hash
$cfg = @{}
foreach ($line in Get-Content $iniPath) {
    if ($line -match '^\s*([A-Za-z]+)\s*=\s*(.*?)\s*$') { $cfg[$Matches[1]] = $Matches[2] }
}
$keys = 'Style','Color','Size','Thickness','Gap','Opacity','Monitor','OffsetX','OffsetY','HotkeyToggle','Outline','DotSize','Visible'
$cfgText = ($keys | ForEach-Object { "$_=$($cfg[$_])" }) -join ' '
Write-Output "ini SHA256 (start): $hashStart"
Report 'ini read' ($cfg.Count -gt 0) $cfgText
$offX = [int]$cfg['OffsetX']; $offY = [int]$cfg['OffsetY']
$cx = 960 + $offX; $cy = 540 + $offY
$colHex = $cfg['Color']
$tR = [Convert]::ToInt32($colHex.Substring(0,2),16)
$tG = [Convert]::ToInt32($colHex.Substring(2,2),16)
$tB = [Convert]::ToInt32($colHex.Substring(4,2),16)
$isTarget = { param($c) ([math]::Abs($c.R - $tR) -le 40) -and ([math]::Abs($c.G - $tG) -le 40) -and ([math]::Abs($c.B - $tB) -le 40) }

# ---------- Step 2: process ----------
$procs = @(Get-Process Crosshair -ErrorAction SilentlyContinue)
$p = $procs | Select-Object -First 1
$cutoff = Get-Date -Hour 17 -Minute 41 -Second 59
$procOk = ($procs.Count -eq 1) -and ($p.Id -eq 18880) -and ($p.Path -ieq $exePath) -and ($p.StartTime -gt $cutoff)
Report 'process' $procOk ("count={0} pid={1} path={2} start={3} exeWrite={4}" -f $procs.Count, $p.Id, $p.Path, $p.StartTime.ToString('HH:mm:ss'), (Get-Item $exePath).LastWriteTime.ToString('HH:mm:ss'))

# ---------- Step 3: window ----------
$hwnd = [Win32.Native]::FindWindow([NullString]::Value, 'CrosshairOverlay')
$found = $hwnd -ne [IntPtr]::Zero
$ex = 0; $vis = $false; $rect = New-Object Win32.Native+RECT
if ($found) {
    $ex = [Win32.Native]::GetWindowLong($hwnd, -20)
    $vis = [Win32.Native]::IsWindowVisible($hwnd)
    [void][Win32.Native]::GetWindowRect($hwnd, [ref]$rect)
}
$flags = @{ LAYERED=0x80000; TRANSPARENT=0x20; TOOLWINDOW=0x80; TOPMOST=0x8; NOACTIVATE=0x8000000 }
$bitsOk = $true; $bitText = @()
foreach ($k in 'LAYERED','TRANSPARENT','TOOLWINDOW','TOPMOST','NOACTIVATE') { $on = (($ex -band $flags[$k]) -ne 0); if (-not $on) { $bitsOk = $false }; $bitText += "$k=$on" }
Report 'window found' $found ('hwnd=0x{0:X}' -f $hwnd.ToInt64())
Report 'ex-style bits' ($found -and $bitsOk) ('GWL_EXSTYLE=0x{0:X8} {1}' -f $ex, ($bitText -join ' '))
Report 'IsWindowVisible' ($found -and $vis) "visible=$vis"
$rcx = ($rect.Left + $rect.Right) / 2.0; $rcy = ($rect.Top + $rect.Bottom) / 2.0
$rectOk = $found -and ([math]::Abs($rcx - $cx) -le 1) -and ([math]::Abs($rcy - $cy) -le 1)
Report 'window rect centered' $rectOk ("rect=({0},{1})-({2},{3}) size={4}x{5} center=({6},{7}) expected=({8},{9})" -f $rect.Left,$rect.Top,$rect.Right,$rect.Bottom,($rect.Right-$rect.Left),($rect.Bottom-$rect.Top),$rcx,$rcy,$cx,$cy)

# ---------- Step 4: pixels ----------
$ox = $cx - 120; $oy = $cy - 120
function Capture-Center([string]$savePath) {
    $bmp = New-Object System.Drawing.Bitmap 240, 240
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($ox, $oy, 0, 0, (New-Object System.Drawing.Size 240, 240))
    $g.Dispose()
    if ($savePath) { $bmp.Save($savePath, [System.Drawing.Imaging.ImageFormat]::Png) }
    return $bmp
}
function Analyze([System.Drawing.Bitmap]$bmp) {
    $n = 0; $sx = 0.0; $sy = 0.0; $pts = New-Object System.Collections.Generic.List[object]
    for ($y = 0; $y -lt 240; $y++) { for ($x = 0; $x -lt 240; $x++) {
        $c = $bmp.GetPixel($x, $y)
        if (& $isTarget $c) { $n++; $sx += ($x + 0.5); $sy += ($y + 0.5); $pts.Add(@($x,$y)) }
    } }
    $cxp = if ($n) { $sx / $n } else { -1 }; $cyp = if ($n) { $sy / $n } else { -1 }
    return @{ count = $n; cx = $cxp; cy = $cyp; pts = $pts }
}
$crop = Capture-Center $pngPath
$a = Analyze $crop
# per-arm / dot breakdown relative to crop center (120,120)
$armUp = 0; $armDown = 0; $armLeft = 0; $armRight = 0; $dot = 0; $other = 0
foreach ($pt in $a.pts) {
    $dx = $pt[0] - 120; $dy = $pt[1] - 120
    if ([math]::Abs($dx) -le 3 -and [math]::Abs($dy) -le 3) { $dot++ }
    elseif ([math]::Abs($dx) -le 2 -and $dy -lt 0) { $armUp++ }
    elseif ([math]::Abs($dx) -le 2 -and $dy -gt 0) { $armDown++ }
    elseif ([math]::Abs($dy) -le 2 -and $dx -lt 0) { $armLeft++ }
    elseif ([math]::Abs($dy) -le 2 -and $dx -gt 0) { $armRight++ }
    else { $other++ }
}
$centerPix = $crop.GetPixel(120,120)
$crop.Dispose()
# expected for CrossDot: 4 arms of (Size-Gap) px x Thickness, plus center dot
$expectedMin = 4 * ([int]$cfg['Size'] - [int]$cfg['Gap']) * [int]$cfg['Thickness'] * 0.6
$styleOk = ($a.count -gt 0) -and ($armUp -gt 5) -and ($armDown -gt 5) -and ($armLeft -gt 5) -and ($armRight -gt 5) -and ($dot -gt 0) -and ($a.count -ge $expectedMin)
$centDx = $a.cx - 120.5; $centDy = $a.cy - 120.5
$centDist = [math]::Sqrt($centDx*$centDx + $centDy*$centDy)
Report 'pixels match color/style' $styleOk ("color={0} matching={1} up={2} down={3} left={4} right={5} dot={6} other={7} expectedMin={8} centerPixel=RGB({9},{10},{11}) png={12}" -f $colHex, $a.count, $armUp, $armDown, $armLeft, $armRight, $dot, $other, $expectedMin, $centerPix.R, $centerPix.G, $centerPix.B, $pngPath)
Report 'pixel centroid' (($a.count -gt 0) -and ($centDist -le 1.5)) ("centroid crop=({0:F2},{1:F2}) screen=({2:F2},{3:F2}) expected=({4},{5}) dist={6:F2}px" -f $a.cx, $a.cy, ($a.cx+$ox), ($a.cy+$oy), ($cx+0.5), ($cy+0.5), $centDist)

# ---------- Step 5: click-through at center + 4 matched points ----------
function First-Match($pts, [scriptblock]$sel) { foreach ($pt in $pts) { if (& $sel $pt) { return $pt } }; return $null }
$upPt    = First-Match $a.pts { param($q) [math]::Abs($q[0]-120) -le 2 -and $q[1] -lt 117 }
$leftPt  = First-Match $a.pts { param($q) [math]::Abs($q[1]-120) -le 2 -and $q[0] -lt 117 }
$rightPt = First-Match $a.pts { param($q) [math]::Abs($q[1]-120) -le 2 -and $q[0] -gt 123 }
$downPt  = First-Match $a.pts { param($q) [math]::Abs($q[0]-120) -le 2 -and $q[1] -gt 123 }
$ctPoints = @(,@(120,120))
foreach ($q in @($upPt, $downPt, $leftPt, $rightPt)) { if ($q -ne $null) { $ctPoints += ,$q } }
$ctOk = ($ctPoints.Count -eq 5); $ctDetail = @()
foreach ($q in $ctPoints) {
    $sx = $q[0] + $ox; $sy = $q[1] + $oy
    $h = [Win32.Native]::WindowFromPoint((New-Object System.Drawing.Point $sx, $sy))
    $rootH = [Win32.Native]::GetAncestor($h, 2)
    $hit = ($h -eq $hwnd) -or ($rootH -eq $hwnd)
    if ($hit) { $ctOk = $false }
    $ctDetail += ('({0},{1})->{2}' -f $sx, $sy, (Describe-Window $h))
}
Report 'click-through' $ctOk (("points={0} " -f $ctPoints.Count) + ($ctDetail -join ' | '))

# ---------- Step 6: toggle (always an even number of presses) ----------
$hk = $cfg['HotkeyToggle']
$sk = ''
if ($hk -match 'Ctrl\+') { $sk += '^' }
if ($hk -match 'Alt\+') { $sk += '%' }
if ($hk -match 'Shift\+') { $sk += '+' }
$keyName = ($hk -split '\+')[-1]
if ($keyName.Length -eq 1) { $sk += $keyName.ToLower() } else { $sk += ('{' + $keyName + '}') }
$script:presses = 0
function Press-Toggle {
    [System.Windows.Forms.SendKeys]::SendWait($sk)
    $script:presses++
    Start-Sleep -Milliseconds 700
    $b = Capture-Center $null
    $r = Analyze $b
    $b.Dispose()
    return @{ n = $r.count; visible = [Win32.Native]::IsWindowVisible($hwnd) }
}
$retried = $false
$off = Press-Toggle
if ($off.n -ne 0 -or $off.visible) {
    $retried = $true
    [void](Press-Toggle); Start-Sleep -Milliseconds 500
    $off = Press-Toggle
}
$on = Press-Toggle
if ($on.n -eq 0 -or -not $on.visible) {
    $retried = $true
    [void](Press-Toggle); Start-Sleep -Milliseconds 500
    $on = Press-Toggle
}
$toggleOk = ($off.n -eq 0) -and (-not $off.visible) -and ($on.n -gt 0) -and $on.visible
Report "toggle $hk ($sk)" $toggleOk ("after hide press: pixels=$($off.n) visible=$($off.visible); after show press: pixels=$($on.n) visible=$($on.visible); retried=$retried; total presses=$($script:presses) (even=$(($script:presses % 2) -eq 0))")

# ---------- Step 7: leak ----------
$p.Refresh()
$hp = $p.Handle
$gdi0 = [Win32.Native]::GetGuiResources($hp, 0); $usr0 = [Win32.Native]::GetGuiResources($hp, 1); $hc0 = $p.HandleCount
Start-Sleep -Seconds 10
$p.Refresh()
$gdi1 = [Win32.Native]::GetGuiResources($hp, 0); $usr1 = [Win32.Native]::GetGuiResources($hp, 1); $hc1 = $p.HandleCount
$leakOk = (($gdi1 - $gdi0) -lt 10) -and (($usr1 - $usr0) -lt 10) -and (($hc1 - $hc0) -lt 10)
Report 'no leaks over 10s' $leakOk ("GDI $gdi0 -> $gdi1 (+$($gdi1-$gdi0)); USER $usr0 -> $usr1 (+$($usr1-$usr0)); handles $hc0 -> $hc1 (+$($hc1-$hc0))")

# ---------- Step 9: final state ----------
$hashEnd = (Get-FileHash -Algorithm SHA256 $iniPath).Hash
$stillRunning = (Get-Process -Id 18880 -ErrorAction SilentlyContinue) -ne $null
$visEnd = [Win32.Native]::IsWindowVisible($hwnd)
$bEnd = Capture-Center $null; $rEnd = Analyze $bEnd; $bEnd.Dispose()
Report 'final: process running' $stillRunning "pid 18880 running=$stillRunning"
Report 'final: ini unchanged' ($hashEnd -eq $hashStart) "start=$hashStart end=$hashEnd"
Report 'final: overlay visible' ($visEnd -and $rEnd.count -gt 0) "visible=$visEnd pixels=$($rEnd.count)"

Write-Output '=== SUMMARY ==='
$script:results | ForEach-Object { Write-Output ("{0} {1}" -f ($(if ($_.passed) {'PASS'} else {'FAIL'})), $_.name) }
