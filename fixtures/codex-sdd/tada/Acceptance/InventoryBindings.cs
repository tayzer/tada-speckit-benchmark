using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Reqnroll;

[assembly: DoNotParallelize]

[Binding]
public sealed class InventoryBindings
{
    private Process? process;
    private HttpClient? http;
    private string id = "";
    private readonly List<int> statuses = [];

    [Given("a reservation of {int} units")]
    public async Task Reserve(int quantity)
    {
        var fixture = TadaAcceptance.Create();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        listener.Stop();
        var start = new ProcessStartInfo(fixture.DotnetHost)
        {
            WorkingDirectory = fixture.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(fixture.SutAssembly);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(address.AbsoluteUri);
        start.Environment["BENCHMARK_DATABASE"] = Path.Combine(fixture.WorkingDirectory, "inventory.sqlite");
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the Inventory API.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!process.HasExited)
        {
            try
            {
                using var ready = await http.GetAsync("/health", deadline.Token);
                if (ready.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) { }
            await Task.Delay(80, deadline.Token);
        }
        using var response = await http.PostAsJsonAsync("/reservations", new { value = quantity }, deadline.Token);
        response.EnsureSuccessStatusCode();
        id = (await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("id").GetString()
            ?? throw new InvalidOperationException("The reservation setup did not produce an ID.");
    }

    [When("the reservation is cancelled")]
    public async Task Cancel()
    {
        using var response = await http!.DeleteAsync("/reservations/" + id);
        statuses.Add((int)response.StatusCode);
    }

    [When("the reservation is resized to {int}")]
    public async Task Resize(int quantity)
    {
        using var response = await http!.PatchAsJsonAsync("/reservations/" + id, new { value = quantity });
        statuses.Add((int)response.StatusCode);
    }

    [Then("both responses have status {int}")]
    public void BothStatuses(int status) => CollectionAssert.AreEqual(new[] { status, status }, statuses.ToArray());

    [Then("the response has status {int}")]
    public void Status(int status) => Assert.AreEqual(status, statuses.Single());

    [Then("the reservation has {int} units")]
    public Task Reservation(int quantity) => AssertNumber("/reservations/" + id, "quantity", quantity);

    [Then("{int} units are available")]
    public Task Stock(int quantity) => AssertNumber("/stock", "available", quantity);

    private async Task AssertNumber(string path, string field, int expected)
    {
        using var response = await http!.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty(field, out var value) && value.TryGetInt32(out _), $"Missing integer {field}.");
        Assert.AreEqual(expected, value.GetInt32());
    }

    [AfterScenario]
    public async Task CleanUp()
    {
        http?.Dispose();
        if (process is null) return;
        try
        {
            if (!process.HasExited) TadaRuntimeCheckpoint.Complete(process);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { process.Dispose(); }
        }
    }
}
