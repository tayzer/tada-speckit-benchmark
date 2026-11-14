# Thin wrapper: builds the harness (Release) when needed, then runs `benchmark <args>`.
# Works on Windows PowerShell 5.1 and PowerShell 7.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'src\Benchmark\bin\Release\net10.0\benchmark.exe'
$sources = Get-ChildItem -Path (Join-Path $root 'src\Benchmark') -Recurse -Include *.cs, *.csproj |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$newest = ($sources | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
if (-not (Test-Path $exe) -or (Get-Item $exe).LastWriteTimeUtc -lt $newest) {
    & dotnet build (Join-Path $root 'src\Benchmark\Benchmark.csproj') -c Release -v q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Harness build failed.' }
}
Push-Location $root
try { & $exe @args; exit $LASTEXITCODE } finally { Pop-Location }
