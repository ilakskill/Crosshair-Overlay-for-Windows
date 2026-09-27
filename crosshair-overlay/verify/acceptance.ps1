# Runtime acceptance test for Crosshair Overlay (spec section 5, items 2-8).
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File acceptance.ps1
# Assumes build.cmd already ran (item 1) and the primary monitor is 1920x1080 at (0,0).
# Note: PowerShell marshals $null string args as "", so FindWindow must receive [NullString]::Value for the class.

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root 'Crosshair.exe'
$verifyDir = Join-Path $root 'verify'
$iniLocal = Join-Path $root 'crosshair.ini'
$iniAppData = Join-Path $env:APPDATA 'CrosshairOverlay\crosshair.ini'

Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
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
    $script:results += [pscustomobject]@{ name = $name; passed = $pass; detail = $detail }
}

function Get-Overlay { [Win32.Native]::FindWindow([NullString]::Value, 'CrosshairOverlay') }

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

# Captures a 240x240 crop centered on (960,540) and returns bitmap plus analysis.
function Capture-Center([string]$savePath) {
    $bmp = New-Object System.Drawing.Bitmap 240, 240
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(840, 420, 0, 0, (New-Object System.Drawing.Size 240, 240))
    $g.Dispose()
    if ($savePath) { $bmp.Save($savePath, [System.Drawing.Imaging.ImageFormat]::Png) }
    return $bmp
}

function Sample-Arms([System.Drawing.Bitmap]$bmp) {
    # Arm points relative to crop: center at (120,120); gap 4 + thickness/2 -> 7px out
    $pts = @(@(120,127), @(120,113), @(127,120), @(113,120))
    $out = @()
    foreach ($p in $pts) {
        $c = $bmp.GetPixel($p[0], $p[1])
        $out += ('({0},{1})=RGB({2},{3},{4})' -f ($p[0]+840), ($p[1]+420), $c.R, $c.G, $c.B)
    }
    return ($out -join ' ')
}

function Count-Color([System.Drawing.Bitmap]$bmp, [scriptblock]$pred) {
    $n = 0
    for ($y = 0; $y -lt $bmp.Height; $y++) {
        for ($x = 0; $x -lt $bmp.Width; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if (& $pred $c) { $n++ }
        }
    }
    return $n
}
$isGreen = { param($c) $c.G -gt 200 -and $c.R -lt 80 -and $c.B -lt 80 }
$isRed   = { param($c) $c.R -gt 200 -and $c.G -lt 80 -and $c.B -lt 80 }

function Arms-Match([System.Drawing.Bitmap]$bmp, [scriptblock]$pred) {
    $pts = @(@(120,127), @(120,113), @(127,120), @(113,120))
    $hits = 0
    foreach ($p in $pts) { if (& $pred $bmp.GetPixel($p[0], $p[1])) { $hits++ } }
    return $hits
}

function Stop-Overlay {
    Stop-Process -Name Crosshair -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

function Remove-Inis {
    Remove-Item $iniLocal -ErrorAction SilentlyContinue
    Remove-Item $iniAppData -ErrorAction SilentlyContinue
}

# ---------- Step 2: clean slate ----------
Stop-Overlay
Remove-Inis
Write-Output "Clean slate: ini removed; Crosshair processes: $((Get-Process Crosshair -ErrorAction SilentlyContinue | Measure-Object).Count)"

# ---------- Step 3: launch ----------
Start-Process -FilePath $exe -WorkingDirectory $root
Start-Sleep -Seconds 2

# ---------- Step 4: window check ----------
$hwnd = Get-Overlay
$found = $hwnd -ne [IntPtr]::Zero
$ex = 0; $vis = $false
if ($found) {
    $ex = [Win32.Native]::GetWindowLong($hwnd, -20)
    $vis = [Win32.Native]::IsWindowVisible($hwnd)
}
$bitsOk = (($ex -band 0x80000) -ne 0) -and (($ex -band 0x20) -ne 0) -and (($ex -band 0x80) -ne 0) -and (($ex -band 0x8) -ne 0)
Report 'window found' $found ('FindWindow(null,"CrosshairOverlay") = 0x{0:X}' -f $hwnd.ToInt64())
Report 'ex-style bits' ($found -and $bitsOk) ('GWL_EXSTYLE = 0x{0:X8}; LAYERED={1} TRANSPARENT={2} TOOLWINDOW={3} TOPMOST={4} NOACTIVATE={5}' -f $ex, (($ex -band 0x80000) -ne 0), (($ex -band 0x20) -ne 0), (($ex -band 0x80) -ne 0), (($ex -band 0x8) -ne 0), (($ex -band 0x8000000) -ne 0))
Report 'IsWindowVisible' ($found -and $vis) "IsWindowVisible = $vis"

# ---------- Step 5: pixel check (default green) ----------
$crop = Capture-Center (Join-Path $verifyDir 'center-default.png')
$armText = Sample-Arms $crop
$greenCount = Count-Color $crop $isGreen
$armHits = Arms-Match $crop $isGreen
$crop.Dispose()
Report 'default green pixels' (($armHits -ge 3) -and ($greenCount -gt 0)) ("arm samples: $armText; arm hits=$armHits/4; green pixels in 240x240 crop=$greenCount")

# ---------- Step 6: click-through ----------
$ctPoints = @(@(960,540), @(960,547), @(960,533), @(967,540), @(953,540))
$ctOk = $true
$ctDetail = @()
foreach ($p in $ctPoints) {
    $h = [Win32.Native]::WindowFromPoint((New-Object System.Drawing.Point $p[0], $p[1]))
    $rootH = [Win32.Native]::GetAncestor($h, 2)
    $hit = ($h -eq $hwnd) -or ($rootH -eq $hwnd)
    if ($hit) { $ctOk = $false }
    $ctDetail += ('({0},{1})->{2}' -f $p[0], $p[1], (Describe-Window $h))
}
Report 'click-through' $ctOk ($ctDetail -join ' | ')

# ---------- Step 7: toggle ----------
function Try-Toggle {
    [System.Windows.Forms.SendKeys]::SendWait('^%x')
    Start-Sleep -Milliseconds 700
    $b = Capture-Center $null
    $g = Count-Color $b $isGreen
    $v = [Win32.Native]::IsWindowVisible($hwnd)
    $b.Dispose()
    return @{ green = $g; visible = $v }
}
$retried = $false
$off = Try-Toggle
if ($off.green -ne 0 -or $off.visible) {
    $retried = $true
    Start-Sleep -Milliseconds 500
    $off = Try-Toggle
}
$on = Try-Toggle
if ($on.green -eq 0 -or -not $on.visible) {
    $retried = $true
    Start-Sleep -Milliseconds 500
    $on = Try-Toggle
}
$toggleOk = ($off.green -eq 0) -and (-not $off.visible) -and ($on.green -gt 0) -and $on.visible
Report 'toggle Ctrl+Alt+X' $toggleOk ("after 1st press: green=$($off.green) visible=$($off.visible); after 2nd press: green=$($on.green) visible=$($on.visible); retried=$retried")

# ---------- Step 8: leak check ----------
$proc = Get-Process Crosshair -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc) {
    $hp = $proc.Handle
    $gdi0 = [Win32.Native]::GetGuiResources($hp, 0)
    $usr0 = [Win32.Native]::GetGuiResources($hp, 1)
    $hc0 = $proc.HandleCount
    Start-Sleep -Seconds 10
    $proc.Refresh()
    $gdi1 = [Win32.Native]::GetGuiResources($hp, 0)
    $usr1 = [Win32.Native]::GetGuiResources($hp, 1)
    $hc1 = $proc.HandleCount
    $leakOk = (($gdi1 - $gdi0) -lt 10) -and (($usr1 - $usr0) -lt 10) -and (($hc1 - $hc0) -lt 10)
    Report 'no leaks over 10s' $leakOk ("GDI $gdi0 -> $gdi1 (+$($gdi1-$gdi0)); USER $usr0 -> $usr1 (+$($usr1-$usr0)); handles $hc0 -> $hc1 (+$($hc1-$hc0))")
} else {
    Report 'no leaks over 10s' $false 'process not found'
}

# ---------- Step 9: config ----------
$iniPath = $null
if (Test-Path $iniLocal) { $iniPath = $iniLocal } elseif (Test-Path $iniAppData) { $iniPath = $iniAppData }
if ($iniPath) {
    $content = Get-Content $iniPath -Raw
    Write-Output "--- ini at $iniPath ---"
    Write-Output $content
    Write-Output '--- end ini ---'
    $hasDefaults = ($content -match '(?m)^\s*Color\s*=\s*00FF00') -and ($content -match '(?m)^\s*Size\s*=\s*12') -and ($content -match '(?m)^\s*Gap\s*=\s*4') -and ($content -match '(?m)^\s*Thickness\s*=\s*2')
    Report 'ini created with defaults' $hasDefaults "path=$iniPath; Color=00FF00,Size=12,Gap=4,Thickness=2 present=$hasDefaults"
} else {
    Report 'ini created with defaults' $false 'neither exe-folder nor APPDATA ini exists after first run'
}

Stop-Overlay
if ($iniPath) {
    $lines = Get-Content $iniPath
    $lines = $lines | ForEach-Object { if ($_ -match '^\s*Color\s*=') { 'Color=FF0000' } else { $_ } }
    Set-Content -Path $iniPath -Value $lines -Encoding ASCII
}
Start-Process -FilePath $exe -WorkingDirectory $root
Start-Sleep -Seconds 2
$crop = Capture-Center (Join-Path $verifyDir 'center-red.png')
$armTextR = Sample-Arms $crop
$redCount = Count-Color $crop $isRed
$redHits = Arms-Match $crop $isRed
$greenLeft = Count-Color $crop $isGreen
$crop.Dispose()
Report 'red after Color=FF0000' (($redHits -ge 3) -and ($redCount -gt 0) -and ($greenLeft -eq 0)) ("arm samples: $armTextR; red arm hits=$redHits/4; red pixels=$redCount; leftover green=$greenLeft")

# ---------- Step 10: clean exit ----------
Stop-Overlay
$left = (Get-Process Crosshair -ErrorAction SilentlyContinue | Measure-Object).Count
$hAfter = Get-Overlay
Report 'clean exit' (($left -eq 0) -and ($hAfter -eq [IntPtr]::Zero)) "Crosshair processes after stop=$left; FindWindow=0x$('{0:X}' -f $hAfter.ToInt64())"
Remove-Inis
Write-Output ("ini cleanup: local exists={0}; appdata exists={1}" -f (Test-Path $iniLocal), (Test-Path $iniAppData))

Write-Output '=== SUMMARY ==='
$script:results | ForEach-Object { Write-Output ("{0} {1}" -f ($(if ($_.passed) {'PASS'} else {'FAIL'})), $_.name) }
