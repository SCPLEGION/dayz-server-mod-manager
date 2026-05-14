<#
.SYNOPSIS
    Builds DayZ Mod Manager (WPF, .NET 8) and packs the output into a zip.

.DESCRIPTION
    Runs `dotnet publish` for the DayZModManager project and zips the result
    into ./dist/DayZModManager-<config>-<rid>-<version>.zip.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER Runtime
    Target runtime identifier. Defaults to win-x64.

.PARAMETER SelfContained
    Publish as self-contained (bundles .NET runtime). Off by default — produces a smaller
    framework-dependent zip (requires .NET 8 Desktop Runtime on the target machine).

.PARAMETER SingleFile
    Publish as a single .exe. Only meaningful with -SelfContained.

.PARAMETER Version
    Version tag for the zip filename. Defaults to the date stamp (yyyyMMdd).

.PARAMETER OutputDir
    Where the final zip is written. Defaults to ./dist.

.PARAMETER SkipClean
    Skip cleaning bin/obj/publish before building.

.EXAMPLE
    pwsh ./scripts/build-and-pack.ps1

.EXAMPLE
    pwsh ./scripts/build-and-pack.ps1 -SelfContained -SingleFile -Version 1.0.0
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string] $Configuration = 'Release',

    [string] $Runtime = 'win-x64',

    [switch] $SelfContained,

    [switch] $SingleFile,

    [string] $Version = (Get-Date -Format 'yyyyMMdd'),

    [string] $OutputDir,

    [switch] $SkipClean
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# ---- Paths ----
$RepoRoot     = Split-Path -Parent $PSScriptRoot
$ProjectDir   = Join-Path $RepoRoot 'DayZModManager'
$ProjectFile  = Join-Path $ProjectDir 'DayZModManager.csproj'

if (-not (Test-Path -LiteralPath $ProjectFile)) {
    throw "Project file not found: $ProjectFile"
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot 'dist'
}

$PublishDir = Join-Path $RepoRoot ("artifacts/publish/{0}-{1}" -f $Configuration, $Runtime)

# ---- Pretty header ----
function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Green
}

Write-Host ""
Write-Host "[SCP] DAYZ // MOD_MANAGER  --  build-and-pack" -ForegroundColor Cyan
Write-Host "    repo       : $RepoRoot"
Write-Host "    project    : $ProjectFile"
Write-Host "    config     : $Configuration"
Write-Host "    runtime    : $Runtime"
Write-Host "    selfcont.  : $([bool]$SelfContained)"
Write-Host "    singlefile : $([bool]$SingleFile)"
Write-Host "    version    : $Version"
Write-Host "    output     : $OutputDir"

# ---- Verify dotnet ----
Write-Step 'Checking dotnet SDK'
$dotnetVersion = & dotnet --version
Write-Host "    dotnet $dotnetVersion"

# ---- Clean ----
if (-not $SkipClean) {
    Write-Step 'Cleaning bin/obj/publish'
    foreach ($p in @(
        (Join-Path $ProjectDir 'bin'),
        (Join-Path $ProjectDir 'obj'),
        (Join-Path $RepoRoot 'artifacts')
    )) {
        if (Test-Path -LiteralPath $p) {
            Remove-Item -LiteralPath $p -Recurse -Force
            Write-Host "    removed $p"
        }
    }
}

# ---- Restore ----
Write-Step 'Restoring NuGet packages'
& dotnet restore $ProjectFile --nologo

# ---- Publish ----
Write-Step 'Publishing'
$publishArgs = @(
    'publish', $ProjectFile,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $PublishDir,
    '--nologo',
    "/p:SelfContained=$([bool]$SelfContained)",
    "/p:PublishSingleFile=$([bool]$SingleFile)"
)
if ($SingleFile -and $SelfContained) {
    $publishArgs += '/p:IncludeNativeLibrariesForSelfExtract=true'
}

& dotnet @publishArgs

if (-not (Test-Path -LiteralPath $PublishDir)) {
    throw "Publish output not found at $PublishDir"
}

# Pull file version from the built exe, if available, for the zip filename.
$exePath = Join-Path $PublishDir 'DayZModManager.exe'
$fileVer = $null
if (Test-Path -LiteralPath $exePath) {
    try {
        $fileVer = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
    } catch {}
}

# ---- Zip ----
Write-Step 'Packing zip'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$sfx       = if ($SelfContained) { 'selfcontained' } else { 'fxdep' }
$zipName   = "DayZModManager-$Configuration-$Runtime-$sfx-$Version.zip"
$zipPath   = Join-Path $OutputDir $zipName

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

# Compress only the published contents (not the parent folder).
Compress-Archive `
    -Path (Join-Path $PublishDir '*') `
    -DestinationPath $zipPath `
    -CompressionLevel Optimal

$zipInfo = Get-Item -LiteralPath $zipPath
$sizeMB  = [math]::Round($zipInfo.Length / 1MB, 2)

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
Write-Host "    zip        : $zipPath"
Write-Host "    size       : $sizeMB MB"
if ($fileVer) {
    Write-Host "    exe ver    : $fileVer"
}
Write-Host ""
