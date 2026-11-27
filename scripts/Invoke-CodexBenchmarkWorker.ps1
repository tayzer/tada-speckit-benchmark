param(
    [Parameter(Mandatory)][string]$CredentialFile,
    [Parameter(Mandatory)][string]$ProfilePath,
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$WorkerRoot,
    [Parameter(Mandatory)][string]$AclStateDirectory,
    [Parameter(Mandatory)][ValidateSet('grant','prepare-metadata','revoke','launch')][string]$Operation
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($WorkerRoot))
$directory = [IO.DirectoryInfo]::new($Workspace)
if (!$directory.FullName.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The workspace must be a child of the configured Windows worker root.'
}
$parents = [Collections.Generic.List[IO.DirectoryInfo]]::new()
for ($ancestor = $directory; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Worker path contains a reparse point at $($ancestor.FullName)."
    }
    if ($ancestor.FullName -ne $directory.FullName) { $parents.Add($ancestor) }
    if ($ancestor.FullName -eq $root) { break }
}
$credential = Import-Clixml -LiteralPath $CredentialFile
if ($credential -isnot [PSCredential]) { throw 'Expected an exported Windows worker PSCredential.' }
$account = [Security.Principal.NTAccount]::new($credential.UserName)
$sid = $account.Translate([Security.Principal.SecurityIdentifier])
$controller = [Security.Principal.WindowsIdentity]::GetCurrent().User
if ($sid -eq $controller) { throw 'Worker and controller must use different Windows accounts.' }

if ($Operation -ne 'launch') {
    $access = [Security.AccessControl.AccessControlSections]::Access
    $stateRoot = [IO.Path]::GetFullPath($AclStateDirectory)
    if ($stateRoot -eq $root -or $stateRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Worker ACL state must be stored outside the worker workspace root.'
    }

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $key = [Convert]::ToHexString($sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($directory.FullName.ToUpperInvariant())))
    } finally { $sha256.Dispose() }
    [IO.Directory]::CreateDirectory($stateRoot) | Out-Null
    $stateFile = Join-Path $stateRoot ($key + '.json')
    $temp = [IO.DirectoryInfo]::new((Join-Path $directory.FullName '.worker-tmp'))
    $roots = [ordered]@{ '.' = $directory; '.worker-tmp' = $temp }
    foreach ($name in '.git', '.agents') {
        $item = [IO.DirectoryInfo]::new((Join-Path $directory.FullName $name))
        if ($item.Exists) { $roots[$name] = $item }
    }

    function Save-WorkspaceAcl {
        param([Collections.IDictionary]$Descriptors)
        $pending = $stateFile + '.tmp'
        [IO.File]::WriteAllText($pending, (ConvertTo-Json -InputObject $Descriptors -Compress))
        [IO.File]::Move($pending, $stateFile, $true)
    }

    function Restore-WorkspaceAcl {
        param([Collections.IDictionary]$Descriptors)
        $names = @($Descriptors.Keys)
        [Array]::Reverse($names)
        $failures = [Collections.Generic.List[string]]::new()
        foreach ($name in $names) {
            try {
                if ($name -notin @('.', '.worker-tmp', '.git', '.agents')) { throw "Invalid saved ACL path $name." }
                if (!$roots.Contains($name)) { continue }
                $item = $roots[$name]
                $item.Refresh()
                if (!$item.Exists) { continue }
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Cannot restore a workspace ACL through a reparse point at $($item.FullName)."
                }
                $acl = [IO.FileSystemAclExtensions]::GetAccessControl($item, $access)
                $acl.SetSecurityDescriptorSddlForm($Descriptors[$name], $access)
                [IO.FileSystemAclExtensions]::SetAccessControl($item, $acl)
            } catch { $failures.Add($_.Exception.Message) }
        }
        if ($failures.Count) { throw ($failures -join ' ') }
    }

    if ($Operation -eq 'grant') {
        if (Test-Path -LiteralPath $stateFile) { throw 'Workspace already has an outstanding ACL grant.' }
        $temp.Create()
        if ($temp.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Worker temp folder is a reparse point.' }
        # Parent entries permit path traversal and attribute checks without granting
        # the ability to list the parent, read sibling files, or inherit access.
        $rights = [Security.AccessControl.FileSystemRights]::Traverse -bor [Security.AccessControl.FileSystemRights]::ReadAttributes
        $identities = @($sid)
        $sandboxGroup = Get-LocalGroup -Name 'CodexSandboxUsers' -ErrorAction SilentlyContinue
        if ($sandboxGroup) { $identities += $sandboxGroup.SID }
        foreach ($ancestor in $parents) {
            $ancestorAcl = [IO.FileSystemAclExtensions]::GetAccessControl($ancestor, $access)
            $changed = $false
            foreach ($identity in $identities) {
                $existing = $ancestorAcl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) |
                    Where-Object {
                        $_.IdentityReference -eq $identity -and $_.AccessControlType -eq 'Allow' -and $_.InheritanceFlags -eq 'None' -and
                        ($_.FileSystemRights -band $rights) -eq $rights
                    }
                if (!$existing) {
                    $ancestorAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                        $identity, $rights, 'None', 'None', 'Allow'))
                    $changed = $true
                }
            }
            if ($changed) { [IO.FileSystemAclExtensions]::SetAccessControl($ancestor, $ancestorAcl) }
        }
        $descriptors = [ordered]@{}
        foreach ($name in '.', '.worker-tmp') {
            $descriptors[$name] = [IO.FileSystemAclExtensions]::GetAccessControl($roots[$name], $access).GetSecurityDescriptorSddlForm($access)
        }
        Save-WorkspaceAcl $descriptors
        try {
            foreach ($name in $descriptors.Keys) {
                $item = $roots[$name]
                $acl = [IO.FileSystemAclExtensions]::GetAccessControl($item, $access)
                $acl.SetAccessRuleProtection($true, $true)
                foreach ($identity in $identities) {
                    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                        $identity, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
                }
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                    $sid, 'ChangePermissions', 'None', 'None', 'Allow'))
                [IO.FileSystemAclExtensions]::SetAccessControl($item, $acl)
            }
        } catch {
            Restore-WorkspaceAcl $descriptors
            [IO.File]::Delete($stateFile)
            throw
        }
    } else {
        $descriptors = Get-Content -Raw -LiteralPath $stateFile | ConvertFrom-Json -AsHashtable
        if (!$descriptors.Contains('.') -or !$descriptors.Contains('.worker-tmp')) { throw 'Invalid saved workspace ACL.' }
        if ($Operation -eq 'prepare-metadata') {
            $metadata = [ordered]@{}
            foreach ($name in '.git', '.agents') {
                if (!$roots.Contains($name) -or $descriptors.Contains($name)) { continue }
                $item = $roots[$name]
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Protected metadata is a reparse point at $($item.FullName)." }
                $acl = [IO.FileSystemAclExtensions]::GetAccessControl($item, $access)
                $descriptors[$name] = $acl.GetSecurityDescriptorSddlForm($access)
                $metadata[$name] = $acl
            }
            Save-WorkspaceAcl $descriptors
            # Only the worker running the elevated helper can install the sandbox's deny rules.
            foreach ($name in $metadata.Keys) {
                $acl = $metadata[$name]
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                    $sid, 'ChangePermissions', 'None', 'None', 'Allow'))
                [IO.FileSystemAclExtensions]::SetAccessControl($roots[$name], $acl)
            }
        } else {
            Restore-WorkspaceAcl $descriptors
            [IO.File]::Delete($stateFile)
        }
    }
    exit 0
}

# A controller or broker exit closes the job handle and stops candidate descendants.
# The worker never receives an inheritable copy of that handle.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class BenchmarkWorkerJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimit value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr handle;

    public BenchmarkWorkerJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception();
        var limit = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref limit, (uint)Marshal.SizeOf(typeof(ExtendedLimit))))
        {
            var error = new Win32Exception();
            Dispose();
            throw error;
        }
    }

    public void Assign(IntPtr process)
    {
        if (!AssignProcessToJobObject(handle, process)) throw new Win32Exception();
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        CloseHandle(handle);
        handle = IntPtr.Zero;
    }
}
'@
# Alternate-user process creation can start on the system drive. Set the working
# directory under the worker identity before launching the requested executable.
$bootstrap = @'
$ErrorActionPreference = 'Stop'
$launch = $env:TADA_BENCHMARK_WORKER_LAUNCH | ConvertFrom-Json -AsHashtable
[Environment]::SetEnvironmentVariable('TADA_BENCHMARK_WORKER_LAUNCH', $null)
[Environment]::CurrentDirectory = $launch.workspace

$toolStart = [Diagnostics.ProcessStartInfo]::new()
$toolStart.FileName = $launch.executable
$toolStart.WorkingDirectory = $launch.workspace
$toolStart.UseShellExecute = $false
$toolStart.CreateNoWindow = $true
$toolStart.RedirectStandardInput = $true
$toolStart.RedirectStandardOutput = $true
$toolStart.RedirectStandardError = $true
foreach ($argument in $launch.arguments) { $toolStart.ArgumentList.Add($argument) }

$tool = [Diagnostics.Process]::Start($toolStart)
try {
    $stdout = $tool.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput())
    $stderr = $tool.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
    try { [Console]::OpenStandardInput().CopyTo($tool.StandardInput.BaseStream) }
    finally { $tool.StandardInput.Close() }
    $tool.WaitForExit()
    if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) {
        throw 'Worker tool output pipes did not close.'
    }
    exit $tool.ExitCode
} finally { $tool.Dispose() }
'@
# CreateProcessWithLogonW limits the command line to 1,024 characters.
# The bootstrap is fixed code and its launch arguments are separate JSON data.
$bootstrapCommand = '$code = $env:TADA_BENCHMARK_WORKER_BOOTSTRAP; Remove-Item Env:TADA_BENCHMARK_WORKER_BOOTSTRAP; & ([scriptblock]::Create($code))'
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json -AsHashtable
if (Test-Path -LiteralPath $request.cancelFile) { exit 125 }
$identity = $credential.GetNetworkCredential()
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $PSHOME 'pwsh.exe'
$start.WorkingDirectory = $Workspace
$start.UserName = $identity.UserName
$start.Domain = if ($identity.Domain) { $identity.Domain } else { '.' }
$start.Password = $credential.Password
$start.LoadUserProfile = $true
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.RedirectStandardInput = $true
$start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', $bootstrapCommand)) {
    $start.ArgumentList.Add($argument)
}
$start.Environment.Clear()
$start.Environment['SystemRoot'] = $env:SystemRoot
$start.Environment['WINDIR'] = $env:WINDIR
$start.Environment['COMSPEC'] = $env:COMSPEC
# NuGet and the .NET SDK use these Windows directories even with an explicit config.
foreach ($name in @('SystemDrive', 'ProgramData', 'ALLUSERSPROFILE', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432')) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value) { $start.Environment[$name] = $value }
}
$start.Environment['USERPROFILE'] = $ProfilePath
# NuGet and SDK tooling use the sandbox's writable temp root for user settings
# and caches. Codex authentication is still resolved from USERPROFILE.
$start.Environment['TEMP'] = Join-Path $Workspace '.worker-tmp'
$start.Environment['TMP'] = $start.Environment['TEMP']
$start.Environment['APPDATA'] = $start.Environment['TEMP']
$start.Environment['LOCALAPPDATA'] = $start.Environment['TEMP']
$start.Environment['PATH'] = $request.path
$start.Environment['PATHEXT'] = '.COM;.EXE;.BAT;.CMD'
$start.Environment['DOTNET_CLI_HOME'] = $start.Environment['TEMP']
$start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
$start.Environment['DOTNET_NOLOGO'] = '1'
$start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
$start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
$start.Environment['DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE'] = 'true'
$start.Environment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH'] = 'false'
$start.Environment['GIT_TERMINAL_PROMPT'] = '0'
$start.Environment['NO_COLOR'] = '1'
$start.Environment['GIT_CONFIG_COUNT'] = '1'
$start.Environment['GIT_CONFIG_KEY_0'] = 'safe.directory'
$start.Environment['GIT_CONFIG_VALUE_0'] = $Workspace.Replace('\','/')
foreach ($entry in $request.environment.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
$start.Environment['TADA_BENCHMARK_WORKER_BOOTSTRAP'] = $bootstrap
$start.Environment['TADA_BENCHMARK_WORKER_LAUNCH'] = @{
    workspace = $Workspace
    executable = $request.executable
    arguments = $request.arguments
} | ConvertTo-Json -Compress
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$timer = [Diagnostics.Stopwatch]::StartNew()
$process = $null
$job = [BenchmarkWorkerJob]::new()
try {
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Could not launch the worker process.' }
    if (!$process.HasExited) { $job.Assign($process.Handle) }
    $stdout = $process.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput())
    $stderr = $process.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
    if ($request.stdin) { $process.StandardInput.Write($request.stdin) }
    $process.StandardInput.Close()
    while (!$process.WaitForExit(150)) {
        if (Test-Path -LiteralPath $request.cancelFile) { $job.Dispose(); $process.WaitForExit(5000) | Out-Null; exit 125 }
        if ($timer.Elapsed.TotalSeconds -ge $request.timeoutSeconds) { $job.Dispose(); $process.WaitForExit(5000) | Out-Null; exit 124 }
    }
    $job.Dispose()
    if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) {
        throw 'Worker output pipes did not close.'
    }
    exit $process.ExitCode
} finally {
    $job.Dispose()
    if ($null -ne $process) {
        if (!$process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}
