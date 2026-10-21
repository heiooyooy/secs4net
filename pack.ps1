<#
.SYNOPSIS
    Packs Secs4Net (HSMS-SS revision, branch hsms-ss) into this repository's LocalPackages folder, then
    copies the package into ALE_SecsGem's LocalPackages folder when that folder is reachable.
    See src/Secs4Net/UPSTREAM.md.

.PARAMETER CopyTo
    The ALE_SecsGem package folder to copy into. Skipped, with a notice, when it does not exist, so the
    script also works on machines without ALE_SecsGem. Pass '' to skip the copy.

.PARAMETER Force
    Overwrite a package of the same version that already exists in CopyTo with different content.
    Without it the copy is refused: consumers that restored that version keep their cached copy, so a
    change needs a new version instead.

.EXAMPLE
    ./pack.ps1
    ./pack.ps1 -CopyTo D:\some\other\feed
    ./pack.ps1 -CopyTo ''
#>
param(
    [string]$CopyTo = 'D:\Atomwork_SVN\Turbo_ALE\Beta_Maintrunk\LocalPackages',
    [string]$Configuration = 'Release',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src/Secs4Net/Secs4Net.csproj'
$output = Join-Path $PSScriptRoot 'LocalPackages'
New-Item -ItemType Directory -Force $output | Out-Null

$version = (dotnet msbuild $project -getProperty:Version -p:Configuration=$Configuration).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { throw "Could not read the package version from $project" }

Write-Host "Packing Secs4Net $version -> $output" -ForegroundColor Cyan
dotnet pack $project -c $Configuration -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed ($LASTEXITCODE)" }

$package = Join-Path $output "Secs4Net.$version.nupkg"
if (-not (Test-Path $package)) { throw "Expected package not found: $package" }

if (-not $CopyTo) {
    Write-Host 'Copy skipped (-CopyTo is empty).' -ForegroundColor Yellow
}
elseif (Test-Path $CopyTo -PathType Container) {
    $target = Join-Path $CopyTo (Split-Path $package -Leaf)
    if ((Test-Path $target) -and -not $Force -and
        (Get-FileHash $target).Hash -ne (Get-FileHash $package).Hash) {
        throw "$target already exists with different content. Bump <Version> in src/Secs4Net/Secs4Net.csproj, or pass -Force to overwrite it."
    }
    Copy-Item $package $CopyTo -Force
    Write-Host "Copied $(Split-Path $package -Leaf) -> $CopyTo" -ForegroundColor Green
}
else {
    Write-Host "Copy skipped: $CopyTo is not reachable. The package stays in $output." -ForegroundColor Yellow
}

Write-Host 'Reminder: bump <Version> in src/Secs4Net/Secs4Net.csproj on every change; NuGet caches a package by id + version.' -ForegroundColor Yellow
