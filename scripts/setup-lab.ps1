# Creates the two isolated lab copies, private/lab-host and private/lab-client. Each one links into the Steam install
# (hardlinks for the three root files, a junction for Cuphead_Data), so it takes almost no disk space, and gets its own
# BepInEx 5.4.23.5 from deps/ plus steam_appid.txt (without it the game restarts itself through Steam).
# Never write game bytes through these links. Re-run after a game update.
param([string]$GameDir = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $GameDir) { $GameDir = $env:CUPHEAD_PATH }
if (-not $GameDir) { $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Cuphead' }
if (-not (Test-Path (Join-Path $GameDir 'Cuphead.exe'))) { throw "Cuphead.exe not found in $GameDir (pass -GameDir)." }
$bepinex = Join-Path $root 'deps\BepInEx-5.4.23.5'
if (-not (Test-Path (Join-Path $bepinex 'BepInEx\core\BepInEx.dll'))) { throw "Unzip BepInEx_win_x64_5.4.23.5.zip into $bepinex first." }

foreach ($name in 'lab-host', 'lab-client') {
    $dir = Join-Path $root "private\$name"
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($f in 'Cuphead.exe', 'UnityPlayer.dll', 'steam_api64.dll') {
        $dst = Join-Path $dir $f
        if (Test-Path $dst) { Remove-Item $dst -Force }
        New-Item -ItemType HardLink -Path $dst -Target (Join-Path $GameDir $f) | Out-Null
    }
    $data = Join-Path $dir 'Cuphead_Data'
    if (-not (Test-Path $data)) { New-Item -ItemType Junction -Path $data -Target (Join-Path $GameDir 'Cuphead_Data') | Out-Null }
    Copy-Item (Join-Path $bepinex '*') $dir -Recurse -Force
    New-Item -ItemType Directory -Force (Join-Path $dir 'BepInEx\plugins\CupheadOnline') | Out-Null
    Set-Content (Join-Path $dir 'steam_appid.txt') '268910' -NoNewline
    # Only the mod's lab save folder; it is seeded from a copy of the player's saves on first run.
    Write-Output "ready: $dir"
}
