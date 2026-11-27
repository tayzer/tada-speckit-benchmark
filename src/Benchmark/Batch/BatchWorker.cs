using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Util;

namespace Benchmark.Batch;

internal sealed class BatchConfiguration
{
    public string WorkerRoot { get; set; } = "";
    public string CredentialFile { get; set; } = "";
    public string WorkerProfile { get; set; } = "";
    public string Dotnet { get; set; } = "";
    public string Codex { get; set; } = "";
    public string Git { get; set; } = "";
    public string PowerShell { get; set; } = "";
    public string NugetFeed { get; set; } = "";
    public string TadaPlugin { get; set; } = "";
    public string TadaPluginSha256 { get; set; } = "";
    public string SpecKitTemplate { get; set; } = "";
    public string SpecKitTemplateSha256 { get; set; } = "";
    public string OpenSpecTemplate { get; set; } = "";
    public string OpenSpecTemplateSha256 { get; set; } = "";
    public string OpenSpecPackage { get; set; } = "";
    public string Node { get; set; } = "";
    public string Approvals { get; set; } = "";
}

internal sealed class BatchWorker(string harness, string controller, BatchConfiguration config, bool trustedLocal = false,
    string model = BatchWorker.DefaultModel)
{
    public const string CodexVersion = "0.162.1";
    public const string SdkVersion = "10.0.403";
    public const string DefaultModel = "gpt-6.2-sol";
    public const string Effort = "medium";
    public BatchOpenSpec? OpenSpec { get; init; }

    public Task AccessAsync(string workspace, bool grant) => ChangeAccessAsync(workspace, grant ? "grant" : "revoke");

    public Task PrepareMetadataAsync(string workspace) => ChangeAccessAsync(workspace, "prepare-metadata");

    private async Task ChangeAccessAsync(string workspace, string operation)
    {
        if (trustedLocal) return;
        var result = await ProcessRunner.RunAsync(Wrapper(workspace, operation) with
        {
            Timeout = TimeSpan.FromSeconds(30)
        });
        if (!result.Succeeded) throw new BenchmarkException("Worker ACL operation failed: " + result.Stderr.Trim());
    }

    public async Task PrepareWorkspaceAsync(string workspace, string log, CancellationToken ct)
    {
        Directory.CreateDirectory(workspace);
        await AccessAsync(workspace, true);
        if (trustedLocal) return;
        try
        {
            // The elevated helper installs an inheritable capability on the root.
            // Materialize the workspace after this step so nested directories inherit it.
            var result = await SandboxedAsync(workspace, config.PowerShell,
                ["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'BENCHMARK_PREFLIGHT_OK'"],
                log, TimeSpan.FromSeconds(40), ct);
            RequireProbe(result, "Workspace sandbox");
        }
        catch
        {
            await AccessAsync(workspace, false);
            throw;
        }
    }

    public async Task<ProcessResult> ExecuteAsync(
        string workspace, string executable, IReadOnlyList<string> arguments, string logBase, TimeSpan timeout, CancellationToken ct,
        string? stdin = null, Dictionary<string, string>? environment = null, bool useOpenSpec = false)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.Combine(workspace, ".worker-tmp"));
        if (trustedLocal)
            return await ProcessRunner.RunAsync(new ProcessSpec(executable, arguments, workspace)
            {
                Environment = environment?.ToDictionary(p => p.Key, p => (string?)p.Value) ?? new Dictionary<string, string?>(),
                Timeout = timeout, StdoutPath = logBase + ".out", StderrPath = logBase + ".err", StdinText = stdin
            }, ct);
        var cancelPath = Path.Combine(controller, ".signals", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(cancelPath)!);
        using var signal = ct.Register(() => File.WriteAllText(cancelPath, "cancel"));
        var variables = environment is null ? new Dictionary<string, string>() : new(environment);
        variables["NUGET_PACKAGES"] = Path.Combine(workspace, ".worker-tmp", "packages");
        var toolDirectories = new[] { config.Dotnet, config.Codex, config.Git, config.PowerShell }
            .Select(Path.GetDirectoryName).Append(Environment.SystemDirectory);
        if (useOpenSpec)
        {
            toolDirectories = toolDirectories.Prepend(BatchOpenSpec.ToolPath(workspace));
            variables["OPENSPEC_TELEMETRY"] = "0";
            variables["OPENSPEC_NO_UPDATE_CHECK"] = "1";
        }
        var request = new
        {
            executable, arguments, stdin, environment = variables,
            timeoutSeconds = timeout.TotalSeconds, cancelFile = cancelPath,
            path = string.Join(Path.PathSeparator, toolDirectories)
        };
        var result = await ProcessRunner.RunAsync(Wrapper(workspace, "launch") with
        {
            StdinText = Json.Serialize(request),
            StdoutPath = logBase + ".out",
            StderrPath = logBase + ".err",
            Timeout = timeout + TimeSpan.FromSeconds(45)
        });
        foreach (string suffix in new[] { ".out", ".err" })
        {
            string path = logBase + suffix;
            if (File.Exists(path)) Redactor.RedactFile(path, path, redact: true);
        }
        return result with
        {
            TimedOut = result.TimedOut || result.ExitCode == 124,
            Cancelled = ct.IsCancellationRequested || result.ExitCode == 125
        };
    }

    private ProcessSpec Wrapper(string workspace, string operation) => new(config.PowerShell,
        ["-NoProfile", "-NonInteractive", "-File", Path.Combine(harness, "scripts", "Invoke-CodexBenchmarkWorker.ps1"),
         "-CredentialFile", config.CredentialFile, "-ProfilePath", config.WorkerProfile,
         "-Workspace", workspace, "-WorkerRoot", config.WorkerRoot, "-AclStateDirectory", Path.Combine(controller, ".acl-state"),
         "-Operation", operation], controller);

    public Task<ProcessResult> DotnetAsync(string workspace, IReadOnlyList<string> args, string log, CancellationToken ct,
        Dictionary<string, string>? variables = null) =>
        SandboxedAsync(workspace, config.Dotnet, args, log, TimeSpan.FromMinutes(5), ct, environment: variables);

    public async Task<ProcessResult> BuildAsync(string workspace, string project, string log, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(workspace, "NuGet.Config")))
            BatchFixtures.ConfigureRestore(workspace, config.NugetFeed);
        var restored = await DotnetAsync(workspace, ["restore", project, "--locked-mode", "--configfile", "NuGet.Config"], log + "-restore", ct);
        if (!restored.Succeeded) return restored;
        return await DotnetAsync(workspace, ["build", project, "-c", "Release", "--no-restore"], log + "-build", ct);
    }

    public List<string> CodexArguments(string? session, bool reviewer = false)
    {
        var args = new List<string> { "-a", "never", "-s", reviewer ? "read-only" : "workspace-write", "exec" };
        if (session is not null) args.Add("resume");
        args.AddRange(["--json", "--model", model, "-c", $"model_reasoning_effort=\"{Effort}\"",
            "-c", "windows.sandbox=\"elevated\"", "-c", "web_search=\"disabled\"",
            "-c", "sandbox_workspace_write.network_access=false", "--ignore-user-config"]);
        if (reviewer) args.AddRange(["--ephemeral", "--skip-git-repo-check"]);
        if (session is not null) args.Add(session);
        args.Add("-");
        return args;
    }

    public Task<ProcessResult> CodexAsync(string workspace, IReadOnlyList<string> args, string prompt, string log, TimeSpan timeout,
        CancellationToken ct, bool useOpenSpec = false, Dictionary<string, string>? environment = null) =>
        ExecuteAsync(workspace, config.Codex, args, log, timeout, ct, stdin: prompt, environment: environment, useOpenSpec: useOpenSpec);

    public async Task CheckOpenSpecAsync(string workspace, string logs, CancellationToken ct)
    {
        var result = await SandboxedAsync(workspace, config.PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "& openspec --version; exit $LASTEXITCODE"], logs,
            TimeSpan.FromSeconds(40), ct, useOpenSpec: true);
        if (!result.Succeeded || result.Stdout.Trim() != BatchOpenSpec.Version)
            throw new BenchmarkException($"Could not run OpenSpec {BatchOpenSpec.Version} under the worker sandbox. {FailureDetail(result)}");
    }

    public Task<ProcessResult> ArchiveOpenSpecAsync(string workspace, string change, string logs, CancellationToken ct) =>
        SandboxedAsync(workspace, config.PowerShell,
            ["-NoProfile", "-NonInteractive", "-CommandWithArgs", "& openspec archive $args[0] --yes --json; exit $LASTEXITCODE", change],
            logs, TimeSpan.FromSeconds(45), ct, useOpenSpec: true);

    public async Task PreflightAsync(string app, string logs, string controllerCanary, string siblingCanary, string evaluatorSource,
        CancellationToken ct, string? unreleasedTicket = null)
    {
        string code = """
            param($Dotnet,$Credential,$ControllerCanary,$SiblingCanary,$EvaluatorSource,$UnreleasedTicket)
            $ErrorActionPreference = 'Stop'
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            if ([Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                throw 'Worker is an administrator.'
            }
            foreach ($path in @($Credential,$ControllerCanary,$SiblingCanary,$EvaluatorSource,$UnreleasedTicket)) {
                if (!$path) { continue }
                try {
                    $file = [IO.File]::OpenRead($path)
                    $file.Dispose()
                } catch [UnauthorizedAccessException] { continue }
                throw "Worker can read a forbidden file: $path"
            }
            $active = Join-Path (Get-Location) 'canary.txt'
            [IO.File]::WriteAllText($active, 'worker')
            [IO.File]::WriteAllText((Join-Path $env:TEMP 'canary.txt'), 'worker temporary files')
            $sdk = & $Dotnet --version
            if ($LASTEXITCODE -ne 0 -or $sdk -ne '10.0.403') { throw "Pinned SDK unavailable: $sdk" }
            $server = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $server.Start()
            try {
                $client = [Net.Sockets.TcpClient]::new()
                try {
                    $client.Connect([Net.IPAddress]::Loopback, $server.LocalEndpoint.Port)
                    $peer = $server.AcceptTcpClient()
                    try {
                        $request = [Text.Encoding]::ASCII.GetBytes("GET /health HTTP/1.0`r`n`r`n")
                        $client.GetStream().Write($request, 0, $request.Length)
                        $reader = [IO.StreamReader]::new($peer.GetStream())
                        if ($reader.ReadLine() -ne 'GET /health HTTP/1.0') { throw 'Loopback HTTP request failed.' }
                        $response = [Text.Encoding]::ASCII.GetBytes("HTTP/1.0 200 OK`r`nContent-Length: 0`r`n`r`n")
                        $peer.GetStream().Write($response, 0, $response.Length)
                        $clientReader = [IO.StreamReader]::new($client.GetStream())
                        if ($clientReader.ReadLine() -ne 'HTTP/1.0 200 OK') { throw 'Loopback HTTP response failed.' }
                    } finally { $peer.Dispose() }
                } finally { $client.Dispose() }
            } finally { $server.Stop() }
            'BENCHMARK_PREFLIGHT_OK'
            """;
        Fs.WriteAtomic(Path.Combine(app, "global.json"), """{"sdk":{"version":"10.0.403","rollForward":"disable","allowPrerelease":false}}""");
        string probe = Path.Combine(app, "preflight.ps1");
        Fs.WriteAtomic(probe, code);
        string[] shell = ["-NoProfile", "-NonInteractive", "-File", probe, config.Dotnet, config.CredentialFile, controllerCanary, siblingCanary, evaluatorSource];
        if (unreleasedTicket is not null) shell = [.. shell, unreleasedTicket];
        var raw = await ExecuteAsync(app, config.PowerShell, shell, Path.Combine(logs, "worker"), TimeSpan.FromSeconds(35), ct);
        RequireProbe(raw, "Worker");
        var version = await ExecuteAsync(app, config.Codex, ["--version"], Path.Combine(logs, "version"), TimeSpan.FromSeconds(15), ct);
        if (!version.Succeeded || version.Stdout.Trim() != $"codex-cli {CodexVersion}")
            throw new BenchmarkException($"Expected codex-cli {CodexVersion}. See version logs.");
        var authentication = await ExecuteAsync(app, config.Codex, ["login", "status"], Path.Combine(logs, "auth"), TimeSpan.FromSeconds(15), ct);
        if (!authentication.Succeeded) throw new BenchmarkException("Codex is not authenticated under the dedicated worker account.");
        await CheckModelAsync(app, logs, ct);
        var sandbox = await ExecuteAsync(app, config.Codex,
            SandboxArguments(config.PowerShell, shell),
            Path.Combine(logs, "sandbox"), TimeSpan.FromSeconds(55), ct);
        RequireProbe(sandbox, "Codex sandbox");
    }

    private async Task CheckModelAsync(string workspace, string logs, CancellationToken ct)
    {
        string probe = Path.Combine(workspace, "model-list.ps1");
        Fs.WriteAtomic(probe, """
            param([string]$Codex)
            $ErrorActionPreference = 'Stop'
            $start = [Diagnostics.ProcessStartInfo]::new($Codex)
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardInput = $true
            $start.RedirectStandardOutput = $true
            $start.ArgumentList.Add('app-server')
            $start.ArgumentList.Add('--stdio')
            $server = [Diagnostics.Process]::Start($start)
            function Read-Reply([int]$Id) {
                while ($null -ne ($line = $server.StandardOutput.ReadLine())) {
                    $reply = $line | ConvertFrom-Json
                    if ($reply.id -ne $Id) { continue }
                    if ($reply.error) { throw $reply.error.message }
                    return $reply.result
                }
                throw 'Codex app-server closed before returning the model catalog.'
            }
            try {
                $server.StandardInput.WriteLine('{"method":"initialize","id":1,"params":{"clientInfo":{"name":"tada-benchmark","version":"1"}}}')
                $null = Read-Reply 1
                $server.StandardInput.WriteLine('{"method":"initialized"}')
                $server.StandardInput.WriteLine('{"method":"model/list","id":2,"params":{"limit":100}}')
                $catalog = Read-Reply 2
                $models = foreach ($entry in $catalog.data) {
                    @{model = $entry.model; efforts = @($entry.supportedReasoningEfforts.reasoningEffort); isDefault = $entry.isDefault}
                }
                ConvertTo-Json -InputObject @($models) -Depth 4 -Compress
            } finally {
                $server.StandardInput.Close()
                if (!$server.WaitForExit(2000)) { $server.Kill($true) }
                $server.Dispose()
            }
            """);
        var result = await ExecuteAsync(workspace, config.PowerShell, ["-NoProfile", "-NonInteractive", "-File", probe, config.Codex],
            Path.Combine(logs, "models"), TimeSpan.FromSeconds(30), ct);
        RequireModel(result, model);
    }

    internal static void RequireModel(ProcessResult result, string model)
    {
        if (!result.Succeeded || Json.TryParseNode(result.Stdout.Trim()) is not JsonArray catalog)
            throw new BenchmarkException($"Could not read the worker's Codex model catalog. {FailureDetail(result)}");
        var supported = catalog.OfType<JsonObject>().Where(entry => entry["efforts"] is JsonArray efforts &&
            efforts.Any(effort => effort?.ToString() == Effort)).Select(entry => entry["model"]?.ToString()).OfType<string>().ToArray();
        if (!supported.Contains(model, StringComparer.Ordinal))
            throw new BenchmarkException($"Codex model {model} at {Effort} effort is unavailable in the worker's catalog. Available models at {Effort} effort: {string.Join(", ", supported)}.");
    }

    public async Task CheckMetadataProtectedAsync(string workspace, string log, CancellationToken ct)
    {
        string code = """
            $ErrorActionPreference = 'Stop'
            foreach ($name in '.git', '.agents') {
                if (!(Test-Path -LiteralPath $name -PathType Container)) { continue }
                $file = Join-Path (Join-Path (Get-Location) $name) ('benchmark-write-canary-' + [Guid]::NewGuid().ToString('N'))
                try {
                    [IO.File]::WriteAllText($file, 'sandbox write probe')
                } catch [UnauthorizedAccessException] { continue }
                throw "Sandbox can write protected metadata in $name."
            }
            'BENCHMARK_PREFLIGHT_OK'
            """;
        var result = await SandboxedAsync(workspace, config.PowerShell, ["-NoProfile", "-NonInteractive", "-Command", code],
            log, TimeSpan.FromSeconds(40), ct);
        RequireProbe(result, "Protected workspace metadata");
    }

    internal static void RequireProbe(ProcessResult result, string name)
    {
        if (result.Succeeded && result.Stdout.Split('\n').Any(line => line.Trim() == "BENCHMARK_PREFLIGHT_OK")) return;
        throw new BenchmarkException($"{name} preflight failed. {FailureDetail(result)}");
    }

    internal static string FailureDetail(ProcessResult result)
    {
        string? detail = result.TimedOut ? "Process timed out."
            : result.LaunchError ?? result.Stderr.Split('\n')
                .Select(line => line.Trim().TrimStart('|').Trim())
                .LastOrDefault(line => line.Length > 0);
        return detail is null ? $"See the process logs (exit {result.ExitCode})."
            : Redactor.Redact(detail, Redactor.SecretEnvironmentValues());
    }

    internal static bool IsSandboxSetupFailure(string error) => error.Contains("windows sandbox failed:", StringComparison.OrdinalIgnoreCase);

    private static string[] SandboxArguments(string executable, IReadOnlyList<string> arguments) =>
        ["-c", "windows.sandbox=\"elevated\"", "-c", "sandbox_workspace_write.network_access=false",
         "sandbox", "--include-managed-config", "--permission-profile", ":workspace", "--", executable, .. arguments];

    private Task<ProcessResult> SandboxedAsync(string workspace, string executable, IReadOnlyList<string> args, string log,
        TimeSpan timeout, CancellationToken ct, Dictionary<string, string>? environment = null, bool useOpenSpec = false) =>
        trustedLocal ? ExecuteAsync(workspace, executable, args, log, timeout, ct, environment: environment)
            : ExecuteAsync(workspace, config.Codex, SandboxArguments(executable, args), log, timeout, ct, environment: environment, useOpenSpec: useOpenSpec);

    public async Task<T> WithApiAsync<T>(string workspace, string assembly, string logs, CancellationToken ct,
        Func<HttpClient, CancellationToken, Task<T>> check, string? databaseFile = null)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        string url = $"http://127.0.0.1:{port}";
        string database = databaseFile ?? Path.Combine(workspace, ".worker-tmp", $"inventory-{Guid.NewGuid():N}.sqlite");
        if (!Fs.IsUnder(database, workspace)) throw new BenchmarkException("Inventory database must remain in its worker workspace.");
        if (File.Exists(database)) Submission.RequireNoLinks(workspace, database);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var server = SandboxedAsync(workspace, config.Dotnet, [assembly, "--urls", url], logs,
            TimeSpan.FromSeconds(30), deadline.Token, environment: new() { ["BENCHMARK_DATABASE"] = database });
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            while (!server.IsCompleted)
            {
                bool ready;
                try
                {
                    using var status = await http.GetAsync("/health", deadline.Token);
                    ready = status.StatusCode == HttpStatusCode.OK;
                }
                catch (HttpRequestException) { ready = false; }
                if (ready) return await check(http, deadline.Token);
                await Task.Delay(100, deadline.Token);
            }
            throw new IOException("Inventory API exited before it became ready.");
        }
        finally
        {
            await deadline.CancelAsync();
            await server;
        }
    }
}
