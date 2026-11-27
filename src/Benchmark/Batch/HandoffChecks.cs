using System.Net.Http.Json;
using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Util;
using static Benchmark.Batch.InventoryFixture;

namespace Benchmark.Batch;

internal static class HandoffChecks
{
    private sealed record Check(string Id, Func<Host, Task> Run);

    private sealed record Host(BatchWorker Worker, string App, string Logs, string? Previous, CancellationToken Token)
    {
        public string Database() => Path.Combine(App, ".worker-tmp", $"handoff-{Guid.NewGuid():N}.sqlite");

        public Task<T> Run<T>(string name, Func<HttpClient, CancellationToken, Task<T>> action, string? database = null,
            bool previous = false) =>
            Worker.WithApiAsync(App, previous ? Previous ?? throw new BenchmarkException("Previous release is required.") : Path.Combine(App, Assembly),
                Path.Combine(Logs, name), Token, action, database);

        public Task Run(string name, Func<HttpClient, CancellationToken, Task> action, string? database = null) =>
            Run(name, async (http, token) => { await action(http, token); return true; }, database);
    }

    private static IEnumerable<Check> Definitions(string ticket)
    {
        foreach (var original in InventoryFixture.Checks("resize").Concat(InventoryFixture.Checks("cancel")).DistinctBy(x => x.Id))
            yield return new Check(original.Id, host => host.Run("request", original.Run));
        yield return new Check("confirmation-input", host => host.Run("request", ConfirmationRules));
        yield return new Check("duplicate-confirmation", host => host.Run("request", DuplicateConfirmation));
        yield return new Check("confirmation-stock", host => host.Run("request", ConfirmationStock));
        yield return new Check("cancel-partial", host => host.Run("request", CancelPartial));
        yield return new Check("legacy-upgrade", Legacy);
        if (ticket == HandoffFixture.First)
        {
            yield return new Check("first-resize-rule", host => host.Run("request", FirstResizeRule));
            yield return new Check("receipt-restart", ReceiptRestart);
        }
        else
        {
            yield return new Check("resize-delta", host => AfterPromotion(host, ResizeDelta));
            yield return new Check("fulfilled-floor", host => AfterPromotion(host, FulfilledFloor));
            yield return new Check("resize-rollback", host => AfterPromotion(host, ResizeRollback));
            yield return new Check("handoff-replay", HandoffReplay);
        }
    }

    public static int Denominator(string ticket) => Definitions(ticket).Count();

    private static string Target(string ticket, string variant) => (ticket, variant) switch
    {
        (HandoffFixture.First, "original") => "confirmation-input",
        (HandoffFixture.First, "duplicate-receipt") => "duplicate-confirmation",
        (HandoffFixture.First, "double-debit") => "confirmation-stock",
        (HandoffFixture.First, "over-restock") => "cancel-partial",
        (HandoffFixture.First, "reset-legacy") => "legacy-upgrade",
        (HandoffFixture.Second, "original") => "resize-delta",
        (HandoffFixture.Second, "floor-bypass") => "fulfilled-floor",
        (HandoffFixture.Second, "wrong-delta") => "resize-delta",
        (HandoffFixture.Second, "partial-write") => "resize-rollback",
        (HandoffFixture.Second, "lost-receipt") => "handoff-replay",
        _ => throw new BenchmarkException("Unknown handoff control variant.")
    };

    public static async Task<List<TestCaseResult>> RunAsync(BatchWorker worker, string app, string logs, string ticket,
        string? previousAssembly, CancellationToken ct, string? negativeVariant = null)
    {
        var checks = Definitions(ticket);
        if (negativeVariant is not null)
        {
            string target = Target(ticket, negativeVariant);
            checks = checks.Where(check => check.Id == target);
        }
        var results = new List<TestCaseResult>();
        foreach (var check in checks)
        {
            ct.ThrowIfCancellationRequested();
            var result = new TestCaseResult { Id = check.Id, Name = check.Id };
            try
            {
                await check.Run(new Host(worker, app, Path.Combine(logs, check.Id), previousAssembly, ct));
                result.Outcome = TestOutcome.Pass;
            }
            catch (FixtureAssertion error)
            {
                result.Outcome = TestOutcome.Fail;
                result.Cause = FailureCause.Behaviour;
                result.Message = error.Message;
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is IOException or HttpRequestException or OperationCanceledException)
            {
                result.Outcome = TestOutcome.Indeterminate;
                result.Cause = FailureCause.Infrastructure;
                result.Message = error.Message;
            }
            results.Add(result);
        }
        return results;
    }

    internal static async Task<HttpResponseMessage> Confirm(HttpClient http, string path, int quantity, string? key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path + "/fulfill") { Content = JsonContent.Create(new { value = quantity }) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await http.SendAsync(request, ct);
    }

    private static async Task Receipt(HttpResponseMessage response, string path, int quantity, int fulfilled, CancellationToken ct)
    {
        CheckStatus(response, 200);
        var body = await ReadBody(response, ct);
        if (!body.TryGetProperty("id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String ||
            Uri.EscapeDataString(id.GetString() ?? "") != path["/reservations/".Length..] ||
            !Integer("quantity", quantity) || !Integer("fulfilledQuantity", fulfilled))
            throw new FixtureAssertion($"Expected the receipt for {path} with total {quantity} and fulfilled quantity {fulfilled}.");

        bool Integer(string field, int expected) =>
            body.TryGetProperty(field, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number &&
            value.TryGetInt32(out int actual) && actual == expected;
    }

    private static async Task State(HttpClient http, string path, int quantity, int fulfilled, int available, CancellationToken ct)
    {
        using var reservation = await http.GetAsync(path, ct);
        await Receipt(reservation, path, quantity, fulfilled, ct);
        await Number(http, "/stock", "available", available, ct);
    }

    private static async Task ConfirmationRules(HttpClient http, CancellationToken ct)
    {
        string path = await Reserve(http, 6, ct);
        using var missingKey = await Confirm(http, path, 2, null, ct);
        CheckStatus(missingKey, 400);
        using var invalid = await Confirm(http, path, 0, "valid-key", ct);
        CheckStatus(invalid, 400);
        using var missing = await Confirm(http, "/reservations/unknown", 1, "valid-key", ct);
        CheckStatus(missing, 404);
        using var tooMany = await Confirm(http, path, 7, "valid-key", ct);
        CheckStatus(tooMany, 409);
        using var valid = await Confirm(http, path, 2, "valid-key", ct);
        await Receipt(valid, path, 6, 2, ct);
        using var mismatch = await Confirm(http, path, 3, "valid-key", ct);
        CheckStatus(mismatch, 409);
        await State(http, path, 6, 2, 4, ct);
    }

    private static async Task DuplicateConfirmation(HttpClient http, CancellationToken ct)
    {
        string path = await Reserve(http, 7, ct);
        var receipts = await Task.WhenAll(Confirm(http, path, 2, "same-shipment", ct), Confirm(http, path, 2, "same-shipment", ct));
        try
        {
            foreach (var receipt in receipts) await Receipt(receipt, path, 7, 2, ct);
            await Number(http, path, "fulfilledQuantity", 2, ct);
        }
        finally { foreach (var receipt in receipts) receipt.Dispose(); }
    }

    private static async Task ConfirmationStock(HttpClient http, CancellationToken ct)
    {
        string first = await Reserve(http, 4, ct);
        string second = await Reserve(http, 3, ct);
        using var a = await Confirm(http, first, 2, "scope-per-reservation", ct);
        await Receipt(a, first, 4, 2, ct);
        using var b = await Confirm(http, second, 1, "scope-per-reservation", ct);
        await Receipt(b, second, 3, 1, ct);
        await Number(http, "/stock", "available", 3, ct);
    }

    private static async Task CancelPartial(HttpClient http, CancellationToken ct)
    {
        string path = await Reserve(http, 6, ct);
        using var shipped = await Confirm(http, path, 2, "dispatched", ct);
        await Receipt(shipped, path, 6, 2, ct);
        using var cancelled = await http.DeleteAsync(path, ct);
        using var repeated = await http.DeleteAsync(path, ct);
        CheckStatus(cancelled, 204);
        CheckStatus(repeated, 204);
        await Number(http, "/stock", "available", 8, ct);
        using var replay = await Confirm(http, path, 2, "dispatched", ct);
        await Receipt(replay, path, 6, 2, ct);
        using var fresh = await Confirm(http, path, 1, "new-shipment", ct);
        CheckStatus(fresh, 404);
        string fully = await Reserve(http, 3, ct);
        using var fullShipment = await Confirm(http, fully, 3, "entire-reservation", ct);
        await Receipt(fullShipment, fully, 3, 3, ct);
        using var fullyCancelled = await http.DeleteAsync(fully, ct);
        CheckStatus(fullyCancelled, 204);
        await Number(http, "/stock", "available", 5, ct);
    }

    private static async Task Legacy(Host host)
    {
        string database = host.Database();
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        File.Copy(Path.Combine(host.App, HandoffFixture.LegacySeed), database);
        await host.Run("open-legacy", async (http, ct) =>
        {
            const string path = "/reservations/legacy-open";
            await State(http, path, 4, 0, 6, ct);
            using var oldCancelled = await http.DeleteAsync("/reservations/legacy-cancelled", ct);
            CheckStatus(oldCancelled, 204);
            using var shipped = await Confirm(http, path, 1, "legacy-shipment", ct);
            await Receipt(shipped, path, 4, 1, ct);
            using var cancelled = await http.DeleteAsync(path, ct);
            CheckStatus(cancelled, 204);
            await Number(http, "/stock", "available", 9, ct);
        }, database);
    }

    private static async Task FirstResizeRule(HttpClient http, CancellationToken ct)
    {
        string path = await SeedShipment(http, ct);
        await Patch(http, path, 4, 409, ct);
        await State(http, path, 6, 2, 4, ct);
    }

    private static async Task ReceiptRestart(Host host)
    {
        string database = host.Database();
        string path = await host.Run("seed", SeedShipment, database);
        await host.Run("restart", async (http, ct) =>
        {
            using var replay = await Confirm(http, path, 2, "shipment-before-handoff", ct);
            await Receipt(replay, path, 6, 2, ct);
            await State(http, path, 6, 2, 4, ct);
        }, database);
    }

    internal static async Task<string> SeedShipment(HttpClient http, CancellationToken ct)
    {
        string path = await Reserve(http, 6, ct);
        using var shipped = await Confirm(http, path, 2, "shipment-before-handoff", ct);
        await Receipt(shipped, path, 6, 2, ct);
        return path;
    }

    private static async Task AfterPromotion(Host host, Func<HttpClient, string, CancellationToken, Task> action)
    {
        string database = host.Database();
        string path = await host.Run("previous", SeedShipment, database, previous: true);
        await host.Run("promoted", (http, ct) => action(http, path, ct), database);
    }

    private static async Task Patch(HttpClient http, string path, int quantity, int status, CancellationToken ct)
    {
        using var response = await http.PatchAsJsonAsync(path, new { value = quantity }, ct);
        CheckStatus(response, status);
        if (status == 200)
        {
            var body = await ReadBody(response, ct);
            if (!body.TryGetProperty("id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String ||
                Uri.EscapeDataString(id.GetString() ?? "") != path["/reservations/".Length..] ||
                !body.TryGetProperty("quantity", out var total) || total.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !total.TryGetInt32(out int value) || value != quantity)
                throw new FixtureAssertion("A successful resize must preserve the existing ID and return the new total.");
        }
    }

    private static async Task ResizeDelta(HttpClient http, string path, CancellationToken ct)
    {
        await Patch(http, path, 4, 200, ct);
        await State(http, path, 4, 2, 6, ct);
        await Patch(http, path, 8, 200, ct);
        await State(http, path, 8, 2, 2, ct);
        await Patch(http, path, 2, 200, ct);
        await State(http, path, 2, 2, 8, ct);
    }

    private static async Task FulfilledFloor(HttpClient http, string path, CancellationToken ct)
    {
        await Patch(http, path, 1, 409, ct);
        await State(http, path, 6, 2, 4, ct);
    }

    private static async Task ResizeRollback(HttpClient http, string path, CancellationToken ct)
    {
        await Patch(http, path, 11, 409, ct);
        await State(http, path, 6, 2, 4, ct);
        using var replay = await Confirm(http, path, 2, "shipment-before-handoff", ct);
        await Receipt(replay, path, 6, 2, ct);
    }

    private static async Task HandoffReplay(Host host)
    {
        string database = host.Database();
        string path = await host.Run("previous", SeedShipment, database, previous: true);
        await host.Run("resize", (http, ct) => Patch(http, path, 4, 200, ct), database);
        await host.Run("restart", async (http, ct) =>
        {
            using var replay = await Confirm(http, path, 2, "shipment-before-handoff", ct);
            await Receipt(replay, path, 6, 2, ct);
            await State(http, path, 4, 2, 6, ct);
            using var first = await http.DeleteAsync(path, ct);
            using var second = await http.DeleteAsync(path, ct);
            CheckStatus(first, 204);
            CheckStatus(second, 204);
            await Number(http, "/stock", "available", 8, ct);
        }, database);
    }

    public static Task CheckPromotedReleaseAsync(BatchWorker worker, string app, string assembly, HandoffPromotion promotion,
        string logs, CancellationToken ct) =>
        worker.WithApiAsync(app, assembly, Path.Combine(logs, "promoted-api"), ct, async (http, token) =>
        {
            string reservation = promotion.Reservation;
            await State(http, reservation, 6, 2, 4, token);
            await Patch(http, reservation, 4, 200, token);
            using var replay = await Confirm(http, reservation, 2, "shipment-before-handoff", token);
            await Receipt(replay, reservation, 6, 2, token);
            await State(http, reservation, 4, 2, 6, token);
            return true;
        }, promotion.Database);
}
