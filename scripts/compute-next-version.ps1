<#
.SYNOPSIS
    Computes the next semantic version tag for auto-release.

.DESCRIPTION
    Looks at the latest `vX.Y.Z` tag (if any) and the commit messages since that
    tag to decide which part of the version to bump:

      - major (X.0.0)  -> commit contains "BREAKING CHANGE" or a "!" conventional
                           commit marker (e.g. "feat!:", "fix!:"), or -Bump major
      - minor (X.Y.0)  -> commit starts with "feat:" / "feat(scope):", or -Bump minor
      - patch (X.Y.Z)  -> anything else (fix/chore/docs/refactor/...), or -Bump patch

    The resulting version shape also doubles as the release channel:

      - X.0.0  -> stable / main release
      - X.Y.0  -> minor / beta release
      - X.Y.Z  -> patch / alpha release (experimental - may or may not work)

.PARAMETER Bump
    Force a specific bump type instead of auto-detecting it from commit messages.
    Defaults to 'auto'.

.EXAMPLE
    pwsh ./scripts/compute-next-version.ps1

.EXAMPLE
    pwsh ./scripts/compute-next-version.ps1 -Bump minor
#>

[CmdletBinding()]
param(
    [ValidateSet('auto', 'major', 'minor', 'patch')]
    [string] $Bump = 'auto'
)

$ErrorActionPreference = 'Stop'

# ---- Find the latest vX.Y.Z tag ----
$tags = git tag -l 'v*' | Where-Object { $_ -match '^v(\d+)\.(\d+)\.(\d+)$' }

$latest = $null
$major = 0
$minor = 0
$patch = 0

if ($tags) {
    $latest = $tags |
        Sort-Object { $_ -match '^v(\d+)\.(\d+)\.(\d+)$' | Out-Null; [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])" } |
        Select-Object -Last 1

    $null = $latest -match '^v(\d+)\.(\d+)\.(\d+)$'
    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3]
}

Write-Host "Latest tag: $(if ($latest) { $latest } else { '(none)' })"

# ---- Decide the bump type ----
if ($Bump -eq 'auto') {
    $range = if ($latest) { "$latest..HEAD" } else { 'HEAD' }
    $commitText = (git log $range --pretty=format:'%s%n%b' 2>$null) -join "`n"

    if ($commitText -match '(?im)^BREAKING CHANGE|^\s*\w+(\([^)]*\))?!:') {
        $Bump = 'major'
    } elseif ($commitText -match '(?im)^\s*feat(\([^)]*\))?:') {
        $Bump = 'minor'
    } else {
        $Bump = 'patch'
    }

    Write-Host "Auto-detected bump: $Bump"
}

switch ($Bump) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}

$newVersion = "v$major.$minor.$patch"

# ---- Release channel purely from the resulting version shape ----
if ($minor -eq 0 -and $patch -eq 0) {
    $channel = 'stable'
} elseif ($patch -eq 0) {
    $channel = 'beta'
} else {
    $channel = 'alpha'
}

Write-Host "Next version: $newVersion ($channel)"

if ($env:GITHUB_OUTPUT) {
    "version=$newVersion" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
    "bump=$Bump" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
    "channel=$channel" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
}
