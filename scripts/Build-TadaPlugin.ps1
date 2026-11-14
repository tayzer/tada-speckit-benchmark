#requires -Version 7.0
<#
.SYNOPSIS
  Builds the TADA Claude Code plugin from the pinned commit and prints the hash to put in experiment.json.

.DESCRIPTION
  TADA's Claude Code packaging exists only in unreleased source (0.1.0-alpha.13 at the pinned commit); the
  published alpha.12 release ships no .claude-plugin manifest. This script follows TADA's documented build
  (docs/plugin.md, docs/quickstart.md). It RUNS TADA'S OWN BUILD SCRIPTS and downloads a checksum-pinned Zig
  compiler into the TADA checkout's .artifacts directory. Review before running. Requires PowerShell 7 and
  .NET SDK 10.0.403 (TADA's source build pin).

  Output: <OutputRoot>\tada (the extracted standalone plugin directory used with `claude --plugin-dir`).
  Nothing is installed into ~/.claude; no marketplace is registered.
#>
param(
    [string]$Commit = '42f1429b0458c5c877b3bf5b093d7f8a10b760a3',
    [string]$OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) '..\BenchmarkData\tada-plugin-build'),
    [string]$Repository = 'https://github.com/tayzer/TADA'
)
$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path (Join-Path $OutputRoot 'tada')) { throw "Output already exists: $OutputRoot\tada (never overwritten). Remove it deliberately to rebuild." }
$src = Join-Path $OutputRoot 'source'
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

git clone --quiet $Repository $src
git -C $src -c advice.detachedHead=false checkout --quiet $Commit
if ((git -C $src rev-parse HEAD) -ne $Commit) { throw 'Pinned commit not checked out.' }

Push-Location $src
try {
    dotnet restore src/Tada.Cli/Tada.Cli.csproj --locked-mode
    dotnet pack src/Tada.Cli/Tada.Cli.csproj -o .artifacts/packages
    $package = Get-ChildItem .artifacts/packages -Filter 'Tada.Cli.*.nupkg' | Sort-Object LastWriteTime | Select-Object -Last 1
    $plugin = & ./scripts/New-TadaPlugin.ps1 -Package $package.FullName
    & ./scripts/Test-TadaPlugin.ps1 -PluginRoot $plugin.pluginRoot -Archive $plugin.archive
    if ($LASTEXITCODE -ne 0) { throw 'TADA plugin self-test failed.' }
    claude plugin validate $plugin.pluginRoot
    Copy-Item -Recurse $plugin.pluginRoot (Join-Path $OutputRoot 'tada')
} finally { Pop-Location }

$benchmark = Join-Path (Split-Path $PSScriptRoot -Parent) 'bench.ps1'
Write-Host "Plugin directory: $(Join-Path $OutputRoot 'tada')"
Write-Host 'Compute the pin with:  .\bench.ps1 doctor   (prints the directory hash) and set workflows[tada].pluginSha256.'
