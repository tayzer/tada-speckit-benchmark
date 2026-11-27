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
    private readonly TadaAcceptance fixture = TadaAcceptance.Create();
    private Process? process;
    private HttpClient? http;
    private string path = "";
    private int status;
    private JsonElement confirmation;

    [Given("an active reservation of {int} units")]
    public async Task Reserve(int quantity)
    {
        await Open();
        using var response = await http!.PostAsJsonAsync("/reservations", new { value = quantity });
        Assert.AreEqual(201, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        path = "/reservations/" + Uri.EscapeDataString(body.GetProperty("id").GetString()!);
    }

    [Given("the legacy inventory database")]
    public async Task Legacy()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "legacy-inventory.dbseed"), Path.Combine(fixture.WorkingDirectory, "inventory.sqlite"));
        await Open();
        path = "/reservations/legacy-open";
    }

    [When("{int} units are confirmed with key {string}")]
    public async Task Confirm(int quantity, string key)
    {
        using var sent = new HttpRequestMessage(HttpMethod.Post, path + "/fulfill") { Content = JsonContent.Create(new { value = quantity }) };
        sent.Headers.Add("Idempotency-Key", key);
        using var response = await http!.SendAsync(sent);
        status = (int)response.StatusCode;
        confirmation = status == 200 ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
    }

    [When("the new reservation total is {int}")]
    public async Task Resize(int quantity)
    {
        using var response = await http!.PatchAsJsonAsync(path, new { value = quantity });
        status = (int)response.StatusCode;
    }

    [When("the active reservation is cancelled")]
    public async Task Cancel()
    {
        using var response = await http!.DeleteAsync(path);
        status = (int)response.StatusCode;
    }

    [When("the inventory API restarts")]
    public async Task Restart()
    {
        await Close();
        await Open();
    }

    [Then("the last response is {int}")]
    public void Status(int expected) => Assert.AreEqual(expected, status);

    [Then("the confirmation records total {int} and fulfilled {int}")]
    public void Receipt(int quantity, int fulfilled)
    {
        Status(200);
        Amount(confirmation, "quantity", quantity);
        Amount(confirmation, "fulfilledQuantity", fulfilled);
    }

    [Then("the reservation has total {int} and fulfilled {int}")]
    public async Task Reservation(int quantity, int fulfilled)
    {
        var body = await Read(path);
        Amount(body, "quantity", quantity);
        Amount(body, "fulfilledQuantity", fulfilled);
    }

    [Then("{int} units are available")]
    public async Task Stock(int expected) => Amount(await Read("/stock"), "available", expected);

    private async Task<JsonElement> Read(string url)
    {
        using var response = await http!.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static void Amount(JsonElement body, string property, int expected)
    {
        Assert.IsTrue(body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out _), $"Expected integer field {property}.");
        Assert.AreEqual(expected, value.GetInt32());
    }

    private async Task Open()
    {
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
        process = Process.Start(start) ?? throw new InvalidOperationException("Inventory API could not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!process.HasExited)
        {
            try
            {
                using var ready = await http.GetAsync("/health", deadline.Token);
                if (ready.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(80, deadline.Token);
        }
        throw new InvalidOperationException("Inventory API exited before becoming ready.");
    }

    [AfterScenario]
    public async Task Close()
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
            finally { process.Dispose(); process = null; }
        }
    }
}
