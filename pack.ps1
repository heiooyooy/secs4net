<#
.SYNOPSIS
    Packs Secs4Net (HSMS-SS revision, branch hsms-ss) into ALE_SecsGem's local package folder.
    See src/Secs4Net/UPSTREAM.md.

.EXAMPLE
    ./pack.ps1
    ./pack.ps1 -Feed D:\some\other\feed
#>
param(
    [string]$Feed = 'D:\Atomwork_SVN\Turbo_ALE\Beta_Maintrunk\ALE_SecsGem\LocalPackages',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Feed)) { throw "Feed folder not found: $Feed" }

$project = Join-Path $PSScriptRoot 'src/Secs4Net/Secs4Net.csproj'
Write-Host "Packing $project -> $Feed" -ForegroundColor Cyan
dotnet pack $project -c $Configuration -o $Feed
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed ($LASTEXITCODE)" }

Write-Host 'Reminder: bump <Version> in src/Secs4Net/Secs4Net.csproj on every change; NuGet caches a package by id + version.' -ForegroundColor Yellow
