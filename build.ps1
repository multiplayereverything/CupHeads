<#
.SYNOPSIS
    CupHeads build + package script.

.DESCRIPTION
    1. Locates the Cuphead install (Steam library folders or CUPHEAD_PATH env-var)
    2. Audits every Harmony hook target against the decompiled game code
    3. Builds the mod DLL (CupheadOnline.csproj)
    4. Packages dist/CupHeads-BepInEx-<version>.zip (BepInEx/plugins/CupheadOnline/CupheadOnline.dll)
    5. Optionally deploys the mod into <Cuphead>/BepInEx/plugins/CupheadOnline

    The BepInEx 5.4.23.5 loader is not part of the mod ZIP; it has its own
    pinned release (bepinex-5.4.23.5) in this repository.

.USAGE
    .\build.ps1 -Release
    .\build.ps1 -Release -NoDeploy
#>
param(
    [string] $CupheadPath = $env:CUPHEAD_PATH,
    [switch] $NoDeploy,
    [switch] $Release
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Configuration      = if ($Release) { "Release" } else { "Debug" }
$Root               = $PSScriptRoot
$DistDir            = Join-Path $Root "dist"
$ModProject         = Join-Path $Root "CupheadOnline\CupheadOnline.csproj"

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "  >>> $msg" -ForegroundColor Cyan
}

function Fail([string]$msg) {
    Write-Host ""
    Write-Host "  FATAL: $msg" -ForegroundColor Red
    exit 1
}

function Invoke-Checked([scriptblock]$Action, [string]$ErrorMessage) {
    & $Action
    if ($LASTEXITCODE -ne 0) { Fail $ErrorMessage }
}

Write-Host ""
Write-Host "  CupHeads build" -ForegroundColor Yellow

Write-Step "Locating Cuphead"

if (-not $CupheadPath) {
    # Every Steam library, not just the default one: libraryfolders.vdf lists
    # the extra libraries (e.g. D:\SteamLibrary) the game may live in.
    $steamRoots = @()
    foreach ($steamKey in @("HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam", "HKCU:\SOFTWARE\Valve\Steam")) {
        if (Test-Path $steamKey) {
            $props = Get-ItemProperty $steamKey
            foreach ($name in @("InstallPath", "SteamPath")) {
                if ($props.PSObject.Properties[$name] -and $props.$name) { $steamRoots += ($props.$name -replace '/', '\') }
            }
        }
    }

    $libraries = @()
    # (Loop variables must not be named $root: PowerShell names are
    # case-insensitive and $Root is the repository root.)
    foreach ($steamRoot in ($steamRoots | Select-Object -Unique)) {
        $libraries += $steamRoot
        $vdf = [System.IO.Path]::Combine($steamRoot, "steamapps\libraryfolders.vdf")
        if ([System.IO.File]::Exists($vdf)) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }

    foreach ($library in ($libraries | Select-Object -Unique)) {
        # Path.Combine, not Join-Path: libraryfolders.vdf can still list a
        # library on a drive that is no longer attached.
        $candidate = [System.IO.Path]::Combine($library, "steamapps\common\Cuphead")
        if ([System.IO.File]::Exists([System.IO.Path]::Combine($candidate, "Cuphead.exe"))) {
            $CupheadPath = $candidate
            break
        }
    }
}

if (-not $CupheadPath) {
    $fallbacks = @(
        "C:\Program Files (x86)\Steam\steamapps\common\Cuphead",
        "C:\Program Files\Steam\steamapps\common\Cuphead",
        "D:\SteamLibrary\steamapps\common\Cuphead",
        "D:\Steam\steamapps\common\Cuphead",
        "E:\SteamLibrary\steamapps\common\Cuphead"
    )
    foreach ($fb in $fallbacks) {
        if (Test-Path "$fb\Cuphead.exe") {
            $CupheadPath = $fb
            break
        }
    }
}

if (-not $CupheadPath -or -not (Test-Path "$CupheadPath\Cuphead.exe")) {
    Write-Host "  Could not auto-detect Cuphead." -ForegroundColor Yellow
    if (-not [Environment]::UserInteractive -or [Console]::IsInputRedirected) {
        Fail "Cuphead not found. Pass -CupheadPath or set CUPHEAD_PATH."
    }
    $CupheadPath = Read-Host "  Enter full path to Cuphead folder"
    if (-not (Test-Path "$CupheadPath\Cuphead.exe")) {
        Fail "Cuphead.exe not found at: $CupheadPath"
    }
}

Write-Host "  Found: $CupheadPath" -ForegroundColor Green
$env:CUPHEAD_PATH = $CupheadPath

Write-Step "Restoring NuGet packages"
Invoke-Checked { dotnet restore $ModProject --nologo } "NuGet restore failed."

Write-Step "Auditing Harmony hook targets"
$AuditScript = Join-Path $Root "tools/audit-hooks.ps1"
& powershell -NoProfile -ExecutionPolicy Bypass -File $AuditScript
if ($LASTEXITCODE -ne 0) {
    Fail "Hook audit failed - a Harmony target or reflected member no longer exists."
}

Write-Step "Building mod DLL ($Configuration)"
Invoke-Checked { dotnet build $ModProject -c $Configuration --nologo --no-restore /p:DeployPluginOnBuild=false } "Mod build failed."

$ModDll = Join-Path $Root "CupheadOnline\bin\$Configuration\net35\CupheadOnline.dll"
if (-not (Test-Path $ModDll)) {
    Fail "CupheadOnline.dll not found after build."
}

Write-Step "Packaging mod ZIP"
$Version = [regex]::Match((Get-Content (Join-Path $Root "CupheadOnline\Plugin.cs") -Raw), 'VERSION\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $Version) { Fail "Could not read PluginInfo.VERSION from Plugin.cs." }
$Stage = Join-Path $DistDir "stage"
Remove-Item $Stage -Recurse -Force -ErrorAction SilentlyContinue
$StagePlugin = Join-Path $Stage "BepInEx\plugins\CupheadOnline"
New-Item -ItemType Directory -Force $StagePlugin | Out-Null
Copy-Item $ModDll (Join-Path $StagePlugin "CupheadOnline.dll") -Force
$Zip = Join-Path $DistDir "CupHeads-BepInEx-$Version.zip"
Remove-Item $Zip -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($Stage, $Zip)
Remove-Item $Stage -Recurse -Force
Copy-Item $ModDll (Join-Path $DistDir "CupheadOnline.dll") -Force
Write-Host "  $Zip" -ForegroundColor Green

if (-not $NoDeploy) {
    Write-Step "Deploying to BepInEx plugin folder"
    if (-not (Test-Path (Join-Path $CupheadPath "BepInEx\core\BepInEx.dll"))) {
        Write-Host "  BepInEx is not installed in $CupheadPath - unzip the bepinex-5.4.23.5 release there first." -ForegroundColor Yellow
    } else {
        $PluginDir = Join-Path $CupheadPath "BepInEx\plugins\CupheadOnline"
        New-Item -ItemType Directory -Force $PluginDir | Out-Null
        Copy-Item $ModDll (Join-Path $PluginDir "CupheadOnline.dll") -Force
        Write-Host "  Deployed to: $PluginDir" -ForegroundColor Green
    }
}
