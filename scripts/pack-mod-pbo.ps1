<#
.SYNOPSIS
    Packs a DayZ mod folder (e.g. @DayZAIBalancer) into PBO files using DayZ Tools AddonBuilder.

.DESCRIPTION
    For each top-level subfolder of the mod (e.g. "scripts"), runs AddonBuilder.exe to
    produce a .pbo inside <ModName>/Addons. Copies mod.cpp / icons / keys alongside.
    Final layout matches what a DayZ server expects under its mods directory.

    Output is also zipped into ./dist/<ModName>-<Version>.zip for distribution.

.PARAMETER ModName
    Name of the mod folder under ./dayz-mod (defaults to @DayZAIBalancer).

.PARAMETER ModSourceDir
    Override the source mod folder path. If not set, uses ./dayz-mod/<ModName>.

.PARAMETER AddonBuilder
    Full path to AddonBuilder.exe. Auto-detected from common DayZ Tools install paths
    if not provided.

.PARAMETER PrivateKey
    Optional .biprivatekey path for signing the resulting PBOs (.bisign files).

.PARAMETER Version
    Version tag for the zip filename. Defaults to date stamp (yyyyMMdd).

.PARAMETER OutputDir
    Where the final zip is written. Defaults to ./dist.

.PARAMETER SkipZip
    Build the @Mod folder but do not produce a zip.

.PARAMETER Clean
    Wipe the build/output @Mod folder before packing.

.EXAMPLE
    pwsh ./scripts/pack-mod-pbo.ps1

.EXAMPLE
    pwsh ./scripts/pack-mod-pbo.ps1 -ModName "@DayZAIBalancer" -PrivateKey "C:\keys\scp.biprivatekey" -Version 1.0.0
#>

[CmdletBinding()]
param(
    [string] $ModName = '@DayZAIBalancer',

    [string] $ModSourceDir,

    [string] $AddonBuilder,

    [string] $PrivateKey,

    [string] $Version = (Get-Date -Format 'yyyyMMdd'),

    [string] $OutputDir,

    [switch] $SkipZip,

    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# ---- Paths ----
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ModSourceDir) {
    $ModSourceDir = Join-Path $RepoRoot "dayz-mod/$ModName"
}
if (-not (Test-Path -LiteralPath $ModSourceDir)) {
    throw "Mod source folder not found: $ModSourceDir"
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot 'dist'
}

$BuildRoot   = Join-Path $RepoRoot 'artifacts/pbo'
$ModBuildDir = Join-Path $BuildRoot $ModName
$AddonsDir   = Join-Path $ModBuildDir 'Addons'
$KeysDir     = Join-Path $ModBuildDir 'Keys'
$TempDir     = Join-Path $BuildRoot '_tmp'
$LogDir      = Join-Path $BuildRoot '_logs'

# ---- Locate AddonBuilder ----
function Find-AddonBuilder {
    $candidates = @(
        'C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools\Bin\AddonBuilder\AddonBuilder.exe',
        'C:\Program Files\Steam\steamapps\common\DayZ Tools\Bin\AddonBuilder\AddonBuilder.exe',
        'D:\Steam\steamapps\common\DayZ Tools\Bin\AddonBuilder\AddonBuilder.exe',
        'E:\Steam\steamapps\common\DayZ Tools\Bin\AddonBuilder\AddonBuilder.exe',
        'C:\Program Files (x86)\Steam\steamapps\common\Arma 3 Tools\AddonBuilder\AddonBuilder.exe'
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

if (-not $AddonBuilder) {
    $AddonBuilder = Find-AddonBuilder
}
if (-not $AddonBuilder -or -not (Test-Path -LiteralPath $AddonBuilder)) {
    throw "AddonBuilder.exe not found. Install DayZ Tools from Steam, or pass -AddonBuilder <path>."
}

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Green
}

Write-Host ""
Write-Host "[SCP] DAYZ // MOD_MANAGER  --  pack-mod-pbo" -ForegroundColor Cyan
Write-Host "    repo       : $RepoRoot"
Write-Host "    mod        : $ModName"
Write-Host "    source     : $ModSourceDir"
Write-Host "    builder    : $AddonBuilder"
Write-Host "    privKey    : $(if ($PrivateKey) { $PrivateKey } else { '<none>' })"
Write-Host "    version    : $Version"
Write-Host "    output     : $OutputDir"

# ---- Clean ----
if ($Clean -and (Test-Path -LiteralPath $ModBuildDir)) {
    Write-Step 'Cleaning previous build'
    Remove-Item -LiteralPath $ModBuildDir -Recurse -Force
    Write-Host "    removed $ModBuildDir"
}

# ---- Prepare layout ----
Write-Step 'Preparing build layout'
New-Item -ItemType Directory -Force -Path $AddonsDir | Out-Null
New-Item -ItemType Directory -Force -Path $TempDir   | Out-Null
New-Item -ItemType Directory -Force -Path $LogDir    | Out-Null

# Copy loose mod files (mod.cpp, README, icons) to the @Mod root.
Get-ChildItem -LiteralPath $ModSourceDir -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $ModBuildDir -Force
    Write-Host "    + $($_.Name)"
}

# ---- Build each top-level subfolder into a PBO ----
$subFolders = Get-ChildItem -LiteralPath $ModSourceDir -Directory
if (-not $subFolders) {
    throw "No subfolders under $ModSourceDir to pack."
}

foreach ($folder in $subFolders) {
    $pboName = "$($folder.Name).pbo"
    Write-Step "Packing $pboName"

    $abArgs = @(
        $folder.FullName,
        $AddonsDir,
        '-temp', $TempDir,
        '-log', (Join-Path $LogDir "$($folder.Name).log"),
        '-clear',
        '-packonly'
    )
    if ($PrivateKey) {
        if (-not (Test-Path -LiteralPath $PrivateKey)) {
            throw "Private key not found: $PrivateKey"
        }
        $abArgs += @('-sign', $PrivateKey)
    }

    & $AddonBuilder @abArgs
    if ($LASTEXITCODE -ne 0) {
        throw "AddonBuilder failed for $($folder.Name) (exit $LASTEXITCODE). See $LogDir."
    }

    $producedPbo = Join-Path $AddonsDir $pboName
    if (-not (Test-Path -LiteralPath $producedPbo)) {
        throw "Expected PBO not produced: $producedPbo"
    }
    $pboSizeKB = [math]::Round((Get-Item -LiteralPath $producedPbo).Length / 1KB, 1)
    Write-Host "    + $pboName ($pboSizeKB KB)"
}

# ---- Copy public key (.bikey) alongside, if signing ----
if ($PrivateKey) {
    $pubKey = [IO.Path]::ChangeExtension($PrivateKey, '.bikey')
    if (Test-Path -LiteralPath $pubKey) {
        New-Item -ItemType Directory -Force -Path $KeysDir | Out-Null
        Copy-Item -LiteralPath $pubKey -Destination $KeysDir -Force
        Write-Host "    + Keys/$(Split-Path $pubKey -Leaf)"
    }
}

# ---- Zip ----
if (-not $SkipZip) {
    Write-Step 'Packing zip'
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    $zipName = "$($ModName.TrimStart('@'))-$Version.zip"
    $zipPath = Join-Path $OutputDir $zipName

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive `
        -Path $ModBuildDir `
        -DestinationPath $zipPath `
        -CompressionLevel Optimal

    $sizeMB = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)
    Write-Host ""
    Write-Host "==> Done." -ForegroundColor Green
    Write-Host "    mod folder : $ModBuildDir"
    Write-Host "    zip        : $zipPath"
    Write-Host "    size       : $sizeMB MB"
} else {
    Write-Host ""
    Write-Host "==> Done (no zip)." -ForegroundColor Green
    Write-Host "    mod folder : $ModBuildDir"
}

Write-Host ""
