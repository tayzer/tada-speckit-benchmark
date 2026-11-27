#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$data = Join-Path $repo '.local/codex-sdd'
$templatePath = Join-Path $data 'worker.example.json'
$configPath = Join-Path $data 'worker.json'
$workerName = 'TadaBenchWorker'
$workerAccount = "$env:COMPUTERNAME\$workerName"
$runnerRoot = Join-Path ([IO.Path]::GetPathRoot($repo)) 'CodexSddRunner'
$workspaceRoot = Join-Path $runnerRoot 'workspaces'
$toolsRoot = Join-Path $runnerRoot 'tools'
$credentialPath = Join-Path $runnerRoot 'worker.clixml'
$pinnedVersion = '0.162.1'
$setupDescription = 'Dedicated TADA Codex benchmark worker'

if ($Preview) {
    Write-Output "Would configure $workerAccount, a standard local Windows account."
    Write-Output "Setup-owned files would live under $runnerRoot."
    Write-Output "Would prepare portable PowerShell 7 when needed and verify Codex CLI $pinnedVersion."
    Write-Output "Would allow the worker read-only access to the tools and staged package feeds."
    Write-Output "Would deny the worker access to the benchmark checkout and evidence."
    Write-Output "Would prompt for the worker password and Codex sign-in, then write $configPath."
    return
}

if ($env:OS -ne 'Windows_NT' -or ![Environment]::Is64BitProcess) {
    throw 'Run this setup from a 64-bit administrator PowerShell on Windows.'
}
$controller = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($controller)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as administrator using your normal benchmark account, then rerun this command.'
}
if (Test-Path -LiteralPath $configPath) {
    throw "Worker configuration already exists at $configPath. Use the batch command with that configuration."
}
if (!(Test-Path -LiteralPath $templatePath -PathType Leaf)) {
    throw "Worker config starter missing at $templatePath. Prepare the pinned inputs first using docs/codex-batch.md."
}
$config = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json
$pinRoot = [IO.Path]::GetFullPath((Join-Path $data 'pins'))
foreach ($directory in @($config.nugetFeed, $config.tadaPlugin, $config.approvals, $config.specKitTemplate, $config.openSpecTemplate)) {
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { throw "Staged benchmark directory is missing: $directory" }
}
foreach ($feed in @($config.nugetFeed, $config.tadaPlugin)) {
    $directory = [IO.Path]::GetFullPath($feed)
    if (!$directory.StartsWith($pinRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The guided setup only grants access to staged feeds under $pinRoot. Use the manual setup for other feed paths."
    }
    while ($directory.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Staged feed path contains a reparse point at $directory."
        }
        $directory = Split-Path -Parent $directory
    }
}
foreach ($executable in @($config.dotnet, $config.git)) {
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Benchmark tool is missing: $executable" }
}
Push-Location $repo
try {
    $sdk = & $config.dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -ne '10.0.403') { throw 'Install .NET SDK 10.0.403 before continuing.' }
} finally { Pop-Location }

$worker = Get-LocalUser -Name $workerName -ErrorAction SilentlyContinue
if ($worker -and $worker.Description -ne $setupDescription) {
    throw "$workerAccount already exists and was not created by this setup. Use the manual worker configuration for that account."
}
$marker = Join-Path $runnerRoot 'controller-sid.txt'
if (Test-Path -LiteralPath $runnerRoot) {
    foreach ($directory in @($runnerRoot, $workspaceRoot, $toolsRoot)) {
        if ((Test-Path -LiteralPath $directory) -and
            ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Runner path contains a reparse point at $directory."
        }
    }
    if (!(Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne $controller.User.Value) {
        throw "$runnerRoot already exists and belongs to another setup. Use the manual worker configuration."
    }
} else {
    New-Item -ItemType Directory -Path $runnerRoot | Out-Null
    Set-Content -LiteralPath $marker -Value $controller.User.Value -Encoding ascii
}

function Set-BenchmarkAcl {
    param([string]$Path, [string[]]$Rules)
    & icacls.exe $Path @Rules
    if ($LASTEXITCODE -ne 0) { throw "Could not configure permissions on $Path." }
}

$controllerSid = '*' + $controller.User.Value
Set-BenchmarkAcl $runnerRoot @('/grant:r', "${controllerSid}:(OI)(CI)(F)", '*S-1-5-18:(OI)(CI)(F)', '*S-1-5-32-544:(OI)(CI)(F)')
Set-BenchmarkAcl $runnerRoot @('/inheritance:r')
New-Item -ItemType Directory -Path $workspaceRoot, $toolsRoot -Force | Out-Null

function Get-PinnedArchive {
    param([string]$Name, [string]$Uri, [string]$Sha256)
    $archive = Join-Path $toolsRoot $Name
    if ((Test-Path -LiteralPath $archive -PathType Leaf) -and
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -eq $Sha256) {
        return $archive
    }

    $staged = Join-Path (Join-Path $pinRoot 'runtime') $Name
    if ((Test-Path -LiteralPath $staged -PathType Leaf) -and
        (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -eq $Sha256) {
        Copy-Item -LiteralPath $staged -Destination $archive
    } else {
        Write-Host "Downloading $Name."
        Invoke-WebRequest -Uri $Uri -OutFile $archive -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Sha256) {
        throw "The $Name download did not match the pinned release SHA-256."
    }
    return $archive
}

function Test-PortablePowerShell {
    param([string]$Path, [string]$Version)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $ErrorActionPreference = 'Continue'
    $observed = & $Path --version 2>$null
    return ($LASTEXITCODE -eq 0 -and $observed -eq "PowerShell $Version")
}

$powerShell = Join-Path $env:ProgramFiles 'PowerShell/7/pwsh.exe'
if (!(Test-Path -LiteralPath $powerShell -PathType Leaf)) {
    $powerShellVersion = '7.7.0'
    $powerShellRoot = Join-Path $toolsRoot "powershell-$powerShellVersion"
    $powerShell = Join-Path $powerShellRoot 'pwsh.exe'
    if (!(Test-PortablePowerShell $powerShell $powerShellVersion)) {
        $asset = "PowerShell-$powerShellVersion-win-x64.zip"
        $archive = Get-PinnedArchive $asset "https://github.com/PowerShell/PowerShell/releases/download/v$powerShellVersion/$asset" `
            '7c74e03b20a75b59715847439d046a6214b1863ee79b187872980f139bb4a05f'
        Write-Host "Extracting portable PowerShell $powerShellVersion under $powerShellRoot."
        Expand-Archive -LiteralPath $archive -DestinationPath $powerShellRoot -Force
        if (!(Test-PortablePowerShell $powerShell $powerShellVersion)) {
            throw "Portable PowerShell did not start from $powerShell. Check the executable's launch error."
        }
    }
}

$cliRoot = Join-Path $toolsRoot "codex-$pinnedVersion"
$cliNames = @('codex.exe', 'codex-x86_64-pc-windows-msvc.exe')
function Find-BenchmarkCodex {
    if (Test-Path -LiteralPath $cliRoot) {
        Get-ChildItem -LiteralPath $cliRoot -Recurse -File |
            Where-Object { $_.Name -in $cliNames } | Select-Object -First 1 -ExpandProperty FullName
    }
}
$codex = Find-BenchmarkCodex
if (!$codex) {
    $asset = 'codex-package-x86_64-pc-windows-msvc.tar.gz'
    $archive = Get-PinnedArchive $asset "https://github.com/openai/codex/releases/download/rust-v$pinnedVersion/$asset" `
        '3e993a82ff393f4530558dcaadb36bb4f0fd1ea7243e621348e40b469f900f6a'
    New-Item -ItemType Directory -Path $cliRoot -Force | Out-Null
    & tar.exe -xzf $archive -C $cliRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the pinned Codex package.' }
    $codex = Find-BenchmarkCodex
}
if (!$codex) { throw 'Codex executable was not found in the pinned package.' }
$actualVersion = & $codex --version
if ($LASTEXITCODE -ne 0 -or $actualVersion -ne "codex-cli $pinnedVersion") {
    throw "Expected Codex CLI $pinnedVersion. Found $actualVersion."
}

$credential = Get-Credential -UserName $workerAccount -Message 'Choose a Windows password for the new benchmark worker, or enter the existing worker password'
if (!$credential -or $credential.UserName -ne $workerAccount) { throw "Enter a credential for $workerAccount." }
if (!$worker) {
    $worker = New-LocalUser -Name $workerName -Password $credential.Password -Description $setupDescription
}
if (!$worker.Enabled) { throw "$workerAccount is disabled." }
$admins = @(Get-LocalGroupMember -SID 'S-1-5-32-544')
if ($admins.SID.Value -contains $worker.SID.Value) { throw 'The benchmark worker must be a standard Windows account.' }
$users = @(Get-LocalGroupMember -SID 'S-1-5-32-545')
if ($users.SID.Value -notcontains $worker.SID.Value) { Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $worker }

$workerSid = '*' + $worker.SID.Value
Set-BenchmarkAcl $runnerRoot @('/grant:r', "${workerSid}:(RX)")
Set-BenchmarkAcl $toolsRoot @('/grant:r', "${workerSid}:(OI)(CI)(RX)")
foreach ($assetRoot in @($config.nugetFeed, $config.tadaPlugin)) {
    Set-BenchmarkAcl $assetRoot @('/inheritance:d')
    Set-BenchmarkAcl $assetRoot @('/grant:r', "${workerSid}:(OI)(CI)(RX)")
}
Set-BenchmarkAcl $repo @('/deny', "${workerSid}:(OI)(CI)(RX)")

# Load the Windows profile using the same credential that the batch broker will use.
$firstLogon = Start-Process -FilePath "$env:SystemRoot/System32/cmd.exe" -ArgumentList '/d', '/c', 'exit', '0' `
    -Credential $credential -LoadUserProfile -WorkingDirectory $toolsRoot -WindowStyle Hidden -PassThru -Wait
try {
    if ($firstLogon.ExitCode -ne 0) { throw "Could not initialize the Windows profile for $workerAccount." }
} finally { $firstLogon.Dispose() }

$profile = Get-CimInstance -ClassName Win32_UserProfile -Filter "SID='$($worker.SID.Value)'" | Select-Object -ExpandProperty LocalPath
if (!$profile -or !(Test-Path -LiteralPath $profile -PathType Container)) {
    throw "Windows did not initialize the profile for $workerAccount. Sign in to that Windows account once and rerun setup."
}
$credential | Export-Clixml -LiteralPath $credentialPath

$setupWorkspace = Join-Path $workspaceRoot ('setup-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path (Join-Path $setupWorkspace '.worker-tmp') | Out-Null
$broker = Join-Path $PSScriptRoot 'Invoke-CodexBenchmarkWorker.ps1'
$brokerArgs = @('-NoProfile', '-NonInteractive', '-File', $broker, '-CredentialFile', $credentialPath,
    '-ProfilePath', $profile, '-Workspace', $setupWorkspace, '-WorkerRoot', $workspaceRoot,
    '-AclStateDirectory', (Join-Path $runnerRoot 'acl-state'))

function Invoke-WorkerCodex {
    param([string[]]$Arguments, [int]$TimeoutSeconds)
    # Windows PowerShell can surface a native program's stderr as an error record.
    # Login status is decided by the exit code, including the expected signed-out result.
    $ErrorActionPreference = 'Continue'
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $request = @{
        executable = $codex
        arguments = $Arguments
        environment = @{}
        path = ((Split-Path -Parent $codex), (Split-Path -Parent $powerShell), "$env:SystemRoot/System32") -join ';'
        timeoutSeconds = $TimeoutSeconds
        cancelFile = Join-Path $runnerRoot 'setup-cancelled'
    } | ConvertTo-Json -Compress
    $request | & $powerShell @brokerArgs -Operation launch
}

& $powerShell @brokerArgs -Operation grant
if ($LASTEXITCODE -ne 0) { throw 'Could not grant the worker its setup workspace.' }
try {
    Invoke-WorkerCodex @('login', 'status') 30
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Follow the Codex device sign-in instructions shown below.'
        Invoke-WorkerCodex @('login', '--device-auth') 600
        if ($LASTEXITCODE -ne 0) {
            throw "Codex sign-in failed. If device sign-in is disabled, sign in to Windows as $workerName and run '$codex' login there, then rerun setup."
        }
    }
    Write-Host 'Provisioning the elevated Codex Windows sandbox.'
    & $codex sandbox setup --elevated --user $workerAccount --codex-home (Join-Path $profile '.codex')
    if ($LASTEXITCODE -ne 0) { throw 'Codex elevated sandbox provisioning failed.' }
    # Codex runs tools under its Windows sandbox identity. The portable tools and
    # pinned package sources are outside the per-run workspace.
    $sandboxGroupSid = '*' + (Get-LocalGroup -Name 'CodexSandboxUsers').SID.Value
    Set-BenchmarkAcl $runnerRoot @('/grant:r', "${sandboxGroupSid}:(X,RA)")
    Set-BenchmarkAcl $toolsRoot @('/grant:r', "${sandboxGroupSid}:(OI)(CI)(RX)")
    Set-BenchmarkAcl $config.nugetFeed @('/grant:r', "${sandboxGroupSid}:(OI)(CI)(RX)")
    Set-BenchmarkAcl (Join-Path $config.tadaPlugin 'feed') @('/grant:r', "${sandboxGroupSid}:(OI)(CI)(RX)")
} finally {
    & $powerShell @brokerArgs -Operation revoke
    if ($LASTEXITCODE -ne 0) { throw 'Could not revoke the worker setup workspace access.' }
}

$config.workerRoot = $workspaceRoot
$config.workerProfile = $profile
$config.credentialFile = $credentialPath
$config.codex = $codex
$config.powerShell = $powerShell
$config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding utf8

Write-Output "Worker configuration saved to $configPath."
if (!(Test-Path -LiteralPath (Join-Path $config.approvals 'approvals.json'))) {
    Write-Output "Review and approve both TADA fixtures under $($config.approvals)."
    Write-Output ("Then run .\bench.ps1 batch-fixtures --out '{0}' --register from the benchmark checkout." -f $config.approvals)
}
Write-Output 'Run .\bench.ps1 batch --suite codex-sdd in your normal PowerShell. The batch verifies the worker before any model calls.'
