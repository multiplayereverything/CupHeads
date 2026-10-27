# Runs one two-player lab tour and judges it with tests/verify-lab.py.
# The copies start minimized in the taskbar (SW_SHOWMINNOACTIVE) as small normal windows (640x360, lowest quality,
# 30 fps, BelowNormal priority) that restore at the right edge of the primary monitor. Placement is set once at launch;
# nothing is activated, made topmost or sent input. Only processes this script started are ever stopped.
# Usage: run-lab.ps1 [-Mutate drop:InputFrame] [-Seconds 240] [-Level Veggies]
param([string]$Mutate = '', [int]$Seconds = 240, [string]$Level = 'Veggies', [int]$Port = 27141)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $root 'CupheadOnline\bin\Release\net35\CupheadOnline.dll'
if (-not (Test-Path $dll)) { throw "Build first: dotnet build CupheadOnline/CupheadOnline.csproj -c Release" }

Add-Type -Namespace CupLab -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetWindowPlacement(System.IntPtr hWnd, ref WINDOWPLACEMENT placement);
[DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern bool SetWindowText(System.IntPtr hWnd, string text);
[DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr hWnd, System.Text.StringBuilder name, int size);
public static bool IsUnity(System.IntPtr h) { var b = new System.Text.StringBuilder(64); GetClassName(h, b, 64); return b.ToString() == "UnityWndClass"; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct POINT { public int X, Y; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT minPosition, maxPosition; public RECT normalPosition; }
'@
Add-Type -AssemblyName System.Windows.Forms
$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

function Park([System.Diagnostics.Process]$p, [int]$slot, [string]$name) {
    $h = $p.MainWindowHandle
    [CupLab.Win]::SetWindowText($h, "Cuphead lab - $name") | Out-Null
    $r = New-Object CupLab.Win+RECT
    [CupLab.Win]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = [Math]::Max(200, $r.Right - $r.Left); $hh = [Math]::Max(150, $r.Bottom - $r.Top)
    $place = New-Object CupLab.Win+WINDOWPLACEMENT
    $place.length = [System.Runtime.InteropServices.Marshal]::SizeOf($place)
    $place.showCmd = 7   # SW_SHOWMINNOACTIVE
    $place.normalPosition.Left = $work.Right - $w; $place.normalPosition.Top = $work.Top + $slot * $hh
    $place.normalPosition.Right = $place.normalPosition.Left + $w; $place.normalPosition.Bottom = $place.normalPosition.Top + $hh
    [CupLab.Win]::SetWindowPlacement($h, [ref]$place) | Out-Null
}

# Unity keeps window size and quality in HKCU\Software\Studio MDHR\Cuphead, shared with the player's own game: back it
# up and put it back afterwards so the lab's 640x360 never sticks.
$regKey = 'HKCU\Software\Studio MDHR\Cuphead'
$regBackup = Join-Path $root 'private\cuphead-prefs-backup.reg'
$hadPrefs = Test-Path 'HKCU:\Software\Studio MDHR\Cuphead'
if ($hadPrefs) { & reg.exe export $regKey $regBackup /y | Out-Null }

$tag = if ($Mutate) { 'mut-' + ($Mutate -replace '[^A-Za-z0-9]+', '-') } else { 'lab' }
$out = Join-Path $root "artifacts\tests\$tag"
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -File | Remove-Item -Force
$started = @()
try {
    $slot = 0
    foreach ($role in 'host', 'client') {
        $dir = Join-Path $root "private\lab-$role"
        if (-not (Test-Path (Join-Path $dir 'BepInEx\core\BepInEx.dll'))) { throw "Run scripts/setup-lab.ps1 first." }
        Copy-Item -Force $dll (Join-Path $dir 'BepInEx\plugins\CupheadOnline\')
        Remove-Item -Recurse -Force (Join-Path $dir 'lab-saves') -ErrorAction SilentlyContinue
        $labArgs = "-screen-fullscreen 0 -screen-width 640 -screen-height 360 -screen-quality Fastest -logFile `"$out\$role-unity.log`" " +
                   "--cupheads-lab $role --cupheads-lab-port $Port --cupheads-lab-level $Level --cupheads-lab-out `"$out\$role.jsonl`""
        if ($Mutate) { $labArgs += " --cupheads-mutate $Mutate" }
        $p = Start-Process -FilePath (Join-Path $dir 'Cuphead.exe') -WorkingDirectory $dir -ArgumentList $labArgs -WindowStyle Minimized -PassThru
        $started += $p
        try { $p.PriorityClass = 'BelowNormal' } catch { }
        for ($i = 0; $i -lt 300; $i++) {
            $p.Refresh()
            if ($p.HasExited) { break }
            if ($p.MainWindowHandle -ne [IntPtr]::Zero -and [CupLab.Win]::IsUnity($p.MainWindowHandle)) { Park $p $slot $role; break }
            Start-Sleep -Milliseconds 100
        }
        $slot++
        if ($role -eq 'host') { Start-Sleep 3 }
    }
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and ($started | Where-Object { -not $_.HasExited })) { Start-Sleep 2 }
}
finally {
    foreach ($p in $started) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    foreach ($p in $started) { $p.WaitForExit(15000) | Out-Null }
    foreach ($role in 'host', 'client') {
        $bep = Join-Path $root "private\lab-$role\BepInEx\LogOutput.log"
        if (Test-Path $bep) { Copy-Item $bep "$out\$role-bepinex.log" -Force }
    }
    if ($hadPrefs) { & reg.exe import $regBackup 2>$null | Out-Null }
    elseif (Test-Path 'HKCU:\Software\Studio MDHR\Cuphead') { Remove-Item 'HKCU:\Software\Studio MDHR\Cuphead' -Recurse -Force }
}
& python -I (Join-Path $root 'tests\verify-lab.py') $out
exit $LASTEXITCODE
