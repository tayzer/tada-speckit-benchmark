using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public sealed class InventoryApi : IAsyncDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("inventory-tests-").FullName;
    private readonly string currentAssembly;
    private Process? process;
    public HttpClient Client { get; private set; } = null!;

    private InventoryApi()
    {
        currentAssembly = Environment.GetEnvironmentVariable("BENCHMARK_API_DLL") ?? DefaultAssembly();
    }

    public static Task<InventoryApi> StartAsync() => CreateAsync(legacy: false);

    public static Task<InventoryApi> StartLegacyAsync() => CreateAsync(legacy: true);

    public static Task<InventoryApi> StartFromPriorReleaseAsync(Func<HttpClient, Task> prepare)
    {
        string assembly = Environment.GetEnvironmentVariable("BENCHMARK_PREVIOUS_API_DLL")
            ?? throw new InvalidOperationException("INDETERMINATE: the previous release is unavailable.");
        return CreateAsync(legacy: false, assembly, prepare);
    }

    private static async Task<InventoryApi> CreateAsync(bool legacy, string? previous = null, Func<HttpClient, Task>? prepare = null)
    {
        var api = new InventoryApi();
        try
        {
            if (legacy)
                File.Copy(Path.Combine(ProjectRoot(), "tests", "AgentTests", "legacy-inventory.dbseed"),
                    Path.Combine(api.directory, "inventory.sqlite"));
            if (previous is not null && prepare is not null)
            {
                await api.LaunchAsync(previous);
                await prepare(api.Client);
                await api.StopAsync();
            }
            await api.LaunchAsync(api.currentAssembly);
            return api;
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await LaunchAsync(currentAssembly);
    }

    private async Task LaunchAsync(string assembly)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        listener.Stop();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(address.AbsoluteUri);
        start.Environment["BENCHMARK_DATABASE"] = Path.Combine(directory, "inventory.sqlite");
        process = Process.Start(start) ?? throw new InvalidOperationException("INDETERMINATE: could not launch Inventory API.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (!process.HasExited)
            {
                try
                {
                    using var response = await Client.GetAsync("/health", deadline.Token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                await Task.Delay(80, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        throw new InvalidOperationException("INDETERMINATE: Inventory API did not become ready.");
    }

    private static string ProjectRoot()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        while (output is not null && !File.Exists(Path.Combine(output.FullName, "Inventory.slnx")))
            output = output.Parent;
        return output?.FullName ?? throw new InvalidOperationException("INDETERMINATE: cannot locate Inventory.slnx.");
    }

    private static string DefaultAssembly()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        return Path.Combine(ProjectRoot(), "src", "Inventory", "bin", configuration, "net10.0", "Inventory.dll");
    }

    private async Task StopAsync()
    {
        Client?.Dispose();
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            process.Dispose();
            process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
