using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public sealed class InventoryApi : IAsyncDisposable
{
    private readonly Process process;
    private readonly string directory;
    public HttpClient Client { get; }

    private InventoryApi(Process process, string directory, Uri address)
    {
        this.process = process;
        this.directory = directory;
        Client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
    }

    public static async Task<InventoryApi> StartAsync()
    {
        string assembly = Environment.GetEnvironmentVariable("BENCHMARK_API_DLL") ?? DefaultAssembly();
        string directory = Directory.CreateTempSubdirectory("inventory-tests-").FullName;
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
        Process process = Process.Start(start) ?? throw new InvalidOperationException("INDETERMINATE: could not launch Inventory API.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var api = new InventoryApi(process, directory, address);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!process.HasExited)
            {
                try
                {
                    using var response = await api.Client.GetAsync("/health", deadline.Token);
                    if (response.IsSuccessStatusCode) return api;
                }
                catch (HttpRequestException) { }
                await Task.Delay(80, deadline.Token);
            }
        }
        catch (OperationCanceledException) { }
        await api.DisposeAsync();
        throw new InvalidOperationException("INDETERMINATE: Inventory API did not become ready.");
    }

    private static string DefaultAssembly()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = output.Parent?.Name ?? "Debug";
        while (output is not null && !File.Exists(Path.Combine(output.FullName, "Inventory.slnx")))
            output = output.Parent;
        return output is null
            ? throw new InvalidOperationException("INDETERMINATE: cannot locate Inventory.slnx.")
            : Path.Combine(output.FullName, "src", "Inventory", "bin", configuration, "net10.0", "Inventory.dll");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            process.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
