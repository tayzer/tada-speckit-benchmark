using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Util;

namespace Benchmark.Batch;

internal static class InventoryFixture
{
    public const string Assembly = "src/Inventory/bin/Release/net10.0/Inventory.dll";
    public const string ApiProject = "src/Inventory/Inventory.csproj";
    public const string TestProject = "tests/AgentTests/AgentTests.csproj";
    public const string ApiSource = "src/Inventory/Program.cs";
    public static readonly string[] Tasks = ["cancel", "resize"];
    public static readonly string[] Variants = ["reference", "original", "fault-a", "fault-b"];
    public static readonly string[] Workflows = ["tada", "speckit", "openspec", "plain"];

    public static string Materialize(string fixture, string destination, string task, string variant, bool tada = false)
    {
        if (!Tasks.Contains(task) || !Variants.Contains(variant)) throw new BenchmarkException("Unknown fixture task or variant.");
        Fs.CopyDirectory(Path.Combine(fixture, "app"), destination);
        File.Copy(Path.Combine(fixture, task + ".md"), Path.Combine(destination, "TASK.md"));
        var program = Path.Combine(destination, ApiSource);
        var source = Fs.NormalizeNewlines(File.ReadAllText(program));
        if (task == "cancel" || variant == "original")
        {
            int first = source.IndexOf("// BEGIN RESIZE\n", StringComparison.Ordinal);
            int last = source.IndexOf("// END RESIZE\n", StringComparison.Ordinal);
            if (first < 0 || last < first) throw new BenchmarkException("Missing resize fixture markers.");
            source = source.Remove(first, last + "// END RESIZE\n".Length - first);
        }
        source = (task, variant) switch
        {
            ("cancel", "original") => ReplaceOnce(source, "    if (!reservation.Active) return Results.NoContent();\n", ""),
            ("cancel", "fault-a") => ReplaceOnce(source, "if (!reservation.Active) return Results.NoContent();", "if (!reservation.Active) return Results.NotFound();"),
            ("cancel", "fault-b") => ReplaceOnce(source, "AddStock(connection, transaction, reservation.Quantity);", "AddStock(connection, transaction, reservation.Quantity - 1);"),
            ("resize", "fault-a") => ReplaceOnce(source, "    AddStock(connection, transaction, -delta);\n", ""),
            ("resize", "fault-b") => ReplaceOnce(source, "    if (delta > Stock(connection, transaction)) return Results.Conflict();", """
                    if (delta > Stock(connection, transaction))
                    {
                        AddStock(connection, transaction, 1);
                        transaction.Commit();
                        return Results.Conflict();
                    }
                """),
            _ => source
        };
        Fs.WriteAtomic(program, source);
        if (tada) OverlayTada(fixture, destination, task);
        return Fs.Sha256Directory(destination);
    }

    public static void OverlayTada(string fixture, string destination, string task)
    {
        var source = Path.Combine(fixture, "tada");
        Fs.CopyDirectory(Path.Combine(source, "Acceptance"), Path.Combine(destination, "Acceptance"));
        Directory.CreateDirectory(Path.Combine(destination, "features"));
        File.Copy(Path.Combine(source, task + ".feature"), Path.Combine(destination, "features", "task.feature"));
        File.Copy(Path.Combine(source, "tada.json"), Path.Combine(destination, "tada.json"));
        Fs.CopyDirectory(Path.Combine(source, ".config"), Path.Combine(destination, ".config"));
    }

    internal static string ReplaceOnce(string source, string before, string after)
    {
        int index = source.IndexOf(before, StringComparison.Ordinal);
        if (index < 0 || source.IndexOf(before, index + before.Length, StringComparison.Ordinal) >= 0)
            throw new BenchmarkException($"Cannot apply unique fixture perturbation: {before.Trim()}");
        return source.Remove(index, before.Length).Insert(index, after);
    }

    public static IReadOnlyList<InventoryCheck> Checks(string task) => task == "cancel"
        ? [new("cancel-once", false, CancelOnce), new("repeat-cancel", false, RepeatCancel),
            new("concurrent-cancel", false, ConcurrentCancel), new("unknown-cancel", false, UnknownCancel), new("create-regression", true, Create),
            new("lookup-regression", true, Lookup)]
        : [new("increase", false, Increase), new("decrease", false, Decrease),
            new("insufficient", false, Insufficient), new("concurrent-resize", false, ConcurrentResize), new("invalid", false, Invalid),
            new("unknown-resize", false, UnknownResize), new("cancelled-resize", false, CancelledResize),
            new("create-regression", true, Create), new("lookup-regression", true, Lookup),
            new("cancel-regression", true, RepeatCancel)];

    public static string[] ExpectedControlFailures(string task, string variant) => (task, variant) switch
    {
        ("cancel", "original" or "fault-a") => ["concurrent-cancel", "repeat-cancel"],
        ("cancel", "fault-b") => ["cancel-once", "concurrent-cancel", "repeat-cancel"],
        ("resize", "original") => ["cancelled-resize", "concurrent-resize", "decrease", "increase", "insufficient", "invalid", "unknown-resize"],
        ("resize", "fault-a") => ["concurrent-resize", "decrease", "increase"],
        ("resize", "fault-b") => ["concurrent-resize", "insufficient"],
        _ => []
    };

    private static async Task Create(HttpClient http, CancellationToken ct)
    {
        using var invalid = await http.PostAsJsonAsync("/reservations", new { value = 0 }, ct);
        CheckStatus(invalid, 400);
        using var excessive = await http.PostAsJsonAsync("/reservations", new { value = 11 }, ct);
        CheckStatus(excessive, 409);
        await Number(http, "/stock", "available", 10, ct);
        await Reserve(http, 3, ct);
        await Number(http, "/stock", "available", 7, ct);
    }

    private static async Task Lookup(HttpClient http, CancellationToken ct)
    {
        string path = await Reserve(http, 3, ct);
        await Number(http, path, "quantity", 3, ct);
        using var missing = await http.GetAsync("/reservations/missing", ct);
        CheckStatus(missing, 404);
    }

    private static async Task CancelOnce(HttpClient http, CancellationToken ct)
    {
        string path = await Reserve(http, 4, ct);
        using var result = await http.DeleteAsync(path, ct);
        CheckStatus(result, 204);
        using var lookup = await http.GetAsync(path, ct);
        CheckStatus(lookup, 404);
        await Number(http, "/stock", "available", 10, ct);
    }

    private static async Task RepeatCancel(HttpClient http, CancellationToken ct)
    {
        string path = await Reserve(http, 3, ct);
        using var first = await http.DeleteAsync(path, ct);
        using var retry = await http.DeleteAsync(path, ct);
        CheckStatus(first, 204);
        CheckStatus(retry, 204);
        await Number(http, "/stock", "available", 10, ct);
    }

    private static async Task ConcurrentCancel(HttpClient http, CancellationToken ct)
    {
        string path = await Reserve(http, 3, ct);
        var responses = await Task.WhenAll(http.DeleteAsync(path, ct), http.DeleteAsync(path, ct));
        try
        {
            foreach (var response in responses) CheckStatus(response, 204);
            await Number(http, "/stock", "available", 10, ct);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private static async Task UnknownCancel(HttpClient http, CancellationToken ct)
    {
        using var response = await http.DeleteAsync("/reservations/missing", ct);
        CheckStatus(response, 404);
        await Number(http, "/stock", "available", 10, ct);
    }

    private static Task Increase(HttpClient http, CancellationToken ct) => Resize(http, 2, 6, 200, 6, 4, ct);
    private static Task Decrease(HttpClient http, CancellationToken ct) => Resize(http, 7, 3, 200, 3, 7, ct);
    private static Task Insufficient(HttpClient http, CancellationToken ct) => Resize(http, 3, 11, 409, 3, 7, ct);
    private static Task Invalid(HttpClient http, CancellationToken ct) => Resize(http, 3, -1, 400, 3, 7, ct);

    private static async Task ConcurrentResize(HttpClient http, CancellationToken ct)
    {
        string first = await Reserve(http, 3, ct);
        string second = await Reserve(http, 3, ct);
        var responses = await Task.WhenAll(http.PatchAsJsonAsync(first, new { value = 6 }, ct),
            http.PatchAsJsonAsync(second, new { value = 6 }, ct));
        try
        {
            if (!responses.Select(response => (int)response.StatusCode).Order().SequenceEqual([200, 409]))
                throw new FixtureAssertion("Concurrent increases must return one HTTP 200 and one HTTP 409.");
            await Number(http, first, "quantity", responses[0].IsSuccessStatusCode ? 6 : 3, ct);
            await Number(http, second, "quantity", responses[1].IsSuccessStatusCode ? 6 : 3, ct);
            await Number(http, "/stock", "available", 1, ct);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private static async Task UnknownResize(HttpClient http, CancellationToken ct)
    {
        using var response = await http.PatchAsJsonAsync("/reservations/missing", new { value = 2 }, ct);
        CheckStatus(response, 404);
        await Number(http, "/stock", "available", 10, ct);
    }

    private static async Task CancelledResize(HttpClient http, CancellationToken ct)
    {
        string path = await Reserve(http, 3, ct);
        using var cancelled = await http.DeleteAsync(path, ct);
        CheckStatus(cancelled, 204);
        using var result = await http.PatchAsJsonAsync(path, new { value = 2 }, ct);
        CheckStatus(result, 404);
        await Number(http, "/stock", "available", 10, ct);
    }

    private static async Task Resize(HttpClient http, int initial, int requested, int status, int quantity, int stock, CancellationToken ct)
    {
        string path = await Reserve(http, initial, ct);
        using var result = await http.PatchAsJsonAsync(path, new { value = requested }, ct);
        CheckStatus(result, status);
        if (status == 200) await Number(result, "quantity", quantity, ct);
        await Number(http, path, "quantity", quantity, ct);
        await Number(http, "/stock", "available", stock, ct);
    }

    internal static async Task<string> Reserve(HttpClient http, int quantity, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/reservations", new { value = quantity }, ct);
        CheckStatus(response, 201);
        var body = await ReadBody(response, ct);
        if (body.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value)
            return "/reservations/" + Uri.EscapeDataString(value);
        throw new FixtureAssertion("Successful reservation did not return an ID.");
    }

    internal static async Task Number(HttpClient http, string path, string field, int expected, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        CheckStatus(response, 200);
        await Number(response, field, expected, ct);
    }

    private static async Task Number(HttpResponseMessage response, string field, int expected, CancellationToken ct)
    {
        var body = await ReadBody(response, ct);
        if (!body.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var actual))
            throw new FixtureAssertion($"Expected an integer {field}.");
        if (actual != expected) throw new FixtureAssertion($"{field}: expected {expected}, received {actual}.");
    }

    internal static async Task<JsonElement> ReadBody(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.ValueKind == JsonValueKind.Object) return body;
        }
        catch (JsonException) { }
        throw new FixtureAssertion("Expected a JSON object.");
    }

    internal static void CheckStatus(HttpResponseMessage response, int expected)
    {
        if ((int)response.StatusCode != expected)
            throw new FixtureAssertion($"Expected HTTP {expected}, received {(int)response.StatusCode}.");
    }
}

internal sealed record InventoryCheck(string Id, bool Regression, Func<HttpClient, CancellationToken, Task> Run);
internal sealed class FixtureAssertion(string message) : Exception(message);
