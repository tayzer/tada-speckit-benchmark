param(
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string]$TadaPlugin
)
$ErrorActionPreference = 'Stop'
$templateRoot = [IO.Path]::GetFullPath($Out)
if (Test-Path -LiteralPath $templateRoot) { throw 'Choose a new output directory.' }
$tadaPath = (Resolve-Path -LiteralPath $TadaPlugin).Path
$manifest = Get-Content -LiteralPath (Join-Path $tadaPath '.codex-plugin/plugin.json') -Raw | ConvertFrom-Json
if ($manifest.name -ne 'tada' -or $manifest.version -ne '0.1.0-alpha.13.107') { throw 'Unexpected TADA plugin release.' }
$specCommit = '0019fa14d12f45edabc2cc3176c9168a68bd0f97'
$spec = New-Item -ItemType Directory -Path "$templateRoot/speckit-source"
$open = New-Item -ItemType Directory -Path "$templateRoot/openspec-source"
Push-Location $spec.FullName
try {
    & uvx --from "git+https://github.com/github/spec-kit.git@$specCommit" specify init --here --force --non-interactive --integration codex --script ps --ignore-agent-tools
    if ($LASTEXITCODE -ne 0) { throw 'Spec Kit setup failed.' }
} finally { Pop-Location }
Push-Location $open.FullName
try {
    $env:OPENSPEC_TELEMETRY = '0'
    $env:OPENSPEC_NO_UPDATE_CHECK = '1'
    $nodePrefix = Join-Path $templateRoot 'openspec-package'
    & npm.cmd install --prefix $nodePrefix --ignore-scripts --no-audit --no-fund --no-save '@fission-ai/openspec@2.0.0'
    if ($LASTEXITCODE -ne 0) { throw 'OpenSpec package installation failed.' }
    $module = Join-Path $nodePrefix 'node_modules/@fission-ai/openspec'
    $package = Get-Content -LiteralPath (Join-Path $module 'package.json') -Raw | ConvertFrom-Json
    if ($package.version -ne '2.0.0' -or !$package.bin.openspec) { throw 'Unexpected OpenSpec package.' }
    & node (Join-Path $module $package.bin.openspec) init --tools codex
    if ($LASTEXITCODE -ne 0) { throw 'OpenSpec setup failed.' }
} finally { Pop-Location }

function Copy-Template([string]$Source, [string]$Name, [string[]]$Directories) {
    $target = New-Item -ItemType Directory -Path (Join-Path $templateRoot $Name)
    foreach ($directory in $Directories) {
        Copy-Item -LiteralPath (Join-Path $Source $directory) -Destination $target.FullName -Recurse
    }
    $target.FullName
}

function Get-TreeHash([string]$Root) {
    $paths = [string[]]@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | ForEach-Object {
        [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
    } | Where-Object { !($_ -match '(^|/)(bin|obj|\.vs|TestResults|\.git)(/|$)') })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $entries = foreach ($relativePath in $paths) {
        $digest = (Get-FileHash -LiteralPath (Join-Path $Root $relativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
        "${relativePath}:$digest"
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($entries -join "`n")
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

$specTemplate = Copy-Template $spec.FullName 'speckit' @('.agents', '.specify')
$openTemplate = Copy-Template $open.FullName 'openspec' @('.agents', 'openspec')
[ordered]@{
    tadaPlugin = $tadaPath
    tadaPluginSha256 = Get-TreeHash $tadaPath
    specKitTemplate = $specTemplate
    specKitTemplateSha256 = Get-TreeHash $specTemplate
    openSpecTemplate = $openTemplate
    openSpecTemplateSha256 = Get-TreeHash $openTemplate
} | ConvertTo-Json | Set-Content -LiteralPath "$templateRoot/template-pins.json"
Write-Output "$templateRoot/template-pins.json"
