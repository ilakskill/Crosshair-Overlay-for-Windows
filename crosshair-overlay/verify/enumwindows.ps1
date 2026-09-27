# Diagnostic: launch the overlay and list every top-level window owned by the Crosshair process.
Add-Type -Namespace Win32 -Name Enum -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
'@
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Stop-Process -Name Crosshair -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Start-Process -FilePath (Join-Path $root 'Crosshair.exe') -WorkingDirectory $root
Start-Sleep -Seconds 2
$target = (Get-Process Crosshair).Id
Write-Output "Crosshair pid = $target"
$cb = [Win32.Enum+EnumProc]{
    param($h, $l)
    $procId = 0
    [void][Win32.Enum]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($procId -eq $target) {
        $t = New-Object System.Text.StringBuilder 256
        $c = New-Object System.Text.StringBuilder 256
        [void][Win32.Enum]::GetWindowText($h, $t, 256)
        [void][Win32.Enum]::GetClassName($h, $c, 256)
        $ex = [Win32.Enum]::GetWindowLong($h, -20)
        Write-Output ('hwnd=0x{0:X} class="{1}" text="{2}" exstyle=0x{3:X8} visible={4}' -f $h.ToInt64(), $c.ToString(), $t.ToString(), $ex, [Win32.Enum]::IsWindowVisible($h))
    }
    return $true
}
[void][Win32.Enum]::EnumWindows($cb, [IntPtr]::Zero)
Write-Output ('FindWindow(null,title) = 0x{0:X}' -f ([Win32.Enum]::FindWindow($null, 'CrosshairOverlay')).ToInt64())
Write-Output ('FindWindow([NullString],title) = 0x{0:X}' -f ([Win32.Enum]::FindWindow([NullString]::Value, 'CrosshairOverlay')).ToInt64())
Stop-Process -Name Crosshair -ErrorAction SilentlyContinue
