#requires -Version 5.1
<#
.SYNOPSIS
  Downloads the pinned published TADA plugin release, verifies its checksum and extracts it for `claude --plugin-dir`.

.DESCRIPTION
  Uses the official GitHub release asset TADA-plugin-<version>.zip (no source build). The zip's SHA-256 is pinned
  here and must also appear in the release's SHA256SUMS. Nothing is installed into ~/.claude; no marketplace is
  registered. Afterwards `.\bench.ps1 doctor` prints the extracted directory hash, which must equal
  workflows[tada].pluginSha256 in experiment.json.
#>
param(
    [string]$Tag = 'v0.1.0-alpha.13.105',
    [string]$Asset = 'TADA-plugin-0.1.0-alpha.13.105.zip',
    [string]$ZipSha256 = 'dbae497d0c61362c0ef017c03c81a23942f763d55bd626a0ca3e314e2782c543',
    [string]$OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) '..\BenchmarkData\tada-release-0.1.0-alpha.13.105'),
    [string]$Repository = 'tayzer/TADA'
)
$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$pluginDir = Join-Path $OutputRoot 'tada'
if (Test-Path $pluginDir) { throw "Output already exists: $pluginDir (never overwritten). Remove it deliberately to re-fetch." }
$download = Join-Path $OutputRoot 'download'
New-Item -ItemType Directory -Force -Path $download | Out-Null

$base = "https://github.com/$Repository/releases/download/$Tag"
Invoke-WebRequest "$base/$Asset" -OutFile (Join-Path $download $Asset) -UseBasicParsing
Invoke-WebRequest "$base/SHA256SUMS" -OutFile (Join-Path $download 'SHA256SUMS') -UseBasicParsing
Invoke-WebRequest "$base/release-manifest.json" -OutFile (Join-Path $download 'release-manifest.json') -UseBasicParsing

$actual = (Get-FileHash (Join-Path $download $Asset) -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $ZipSha256) { throw "Checksum mismatch for ${Asset}: $actual (pinned $ZipSha256)." }
$listed = Select-String -Path (Join-Path $download 'SHA256SUMS') -SimpleMatch $Asset | ForEach-Object { ($_.Line -split '\s+')[0].ToLowerInvariant() }
if ($listed -notcontains $ZipSha256) { throw "$Asset checksum not listed in the release SHA256SUMS." }

Expand-Archive (Join-Path $download $Asset) -DestinationPath $pluginDir
if (-not (Test-Path (Join-Path $pluginDir '.claude-plugin\plugin.json'))) { throw 'Extracted plugin has no .claude-plugin/plugin.json.' }
Write-Host "Plugin directory: $pluginDir"
Write-Host 'Run .\bench.ps1 doctor and check the TADA plugin hash matches workflows[tada].pluginSha256.'
