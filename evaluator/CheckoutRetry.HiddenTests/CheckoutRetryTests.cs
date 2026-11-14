using CheckoutRetry.HiddenTests.Infrastructure;
using Xunit;

namespace CheckoutRetry.HiddenTests;

/// <summary>
/// DRAFT hidden behavioural tests for "duplicate orders when checkout is retried after a timeout".
/// Expectations map to the owner-approved decisions in experiments/checkout-retry/decisions.json. Tests observe only HTTP behaviour and persisted rows (Orders/OrderItems/
/// Baskets tables), never internal types, so any reasonable design can pass.
/// Outcome conventions: assertion failure = FAIL; message starting "INDETERMINATE:" = the scenario could not be
/// exercised against this implementation; skip with "PENDING-DECISION" = awaiting an owner decision.
/// </summary>
public sealed class CheckoutRetryTests
{
    private const string Demo = "demouser@microsoft.com";
    private const string Other = "productmgr@microsoft.com";

    /// <summary>D7: a submission from a reloaded checkout page while the first is still in flight is a retry (1 order).</summary>
    private static readonly int? D7ExpectedOrdersForFreshPageRetry = 1;

    private static async Task<(EShopHost Host, ShopClient Customer, Item Item)> ArrangeAsync(string email = Demo)
    {
        var host = await EShopHost.StartAsync();
        var customer = new ShopClient(host, email);
        await customer.LoginAsync();
        var item = await Item.FirstAsync(host);
        await customer.AddItemAsync(item.Id, item.Name, item.Price);
        return (host, customer, item);
    }

    private static void RequireSeam(EShopHost host, string scenario)
    {
        if (host.Faults.FaultsFired == 0)
            Assert.Fail($"INDETERMINATE: {scenario}: the order insert was not observable at the database boundary (no EF Core command inserting into [Orders]); scenario not exercised.");
    }

    [Fact] // D1, D2
    public async Task HT01_ResubmitAfterCompletedCheckout_NoDuplicateOrder()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        var first = await customer.SubmitCheckoutAsync(form);
        Assert.True(first.LandedOnConfirmation, "First checkout did not complete.");
        await customer.SubmitCheckoutAsync(form); // browser resends the same form
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));
    }

    [Fact] // D3 — critical
    public async Task HT02_ConcurrentDuplicateSubmissions_CreateExactlyOneOrder()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.DelayFirstOrderInsert, TimeSpan.FromSeconds(2));
        var a = customer.SubmitCheckoutAsync(form);
        await Task.Delay(300);
        var b = customer.SubmitCheckoutAsync(form);
        await Task.WhenAll(a, b);
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));
    }

    [Fact] // D3 — the task's headline scenario — critical
    public async Task HT03_RetryAfterClientTimeout_CreatesExactlyOneOrder()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.DelayFirstOrderInsert, TimeSpan.FromSeconds(4));
        // The customer gives up after 1s (TestServer does not abandon a request on client cancellation, so the
        // timeout is modelled by not waiting for the first response). The server keeps processing it.
        var first = customer.SubmitCheckoutAsync(form);
        var completedInTime = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(1))) == first;
        if (completedInTime)
            Assert.Fail("INDETERMINATE: the first attempt completed before the simulated client timeout; timeout scenario not exercised.");
        var retry = await customer.SubmitCheckoutAsync(form);
        await first;
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        RequireSeam(host, "retry after timeout");
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));
        Assert.False(retry.ServerError, "The retry ended in a server error.");
    }

    [Fact] // D4
    public async Task HT04_FailureBeforePersistence_RetryCreatesExactlyOneOrder()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.FailFirstOrderInsert);
        await customer.SubmitCheckoutAsync(form);
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        RequireSeam(host, "failure before persistence");
        Assert.Equal(0, await host.Db.OrderCountAsync(Demo));
        await customer.SubmitCheckoutAsync(form);
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));
    }

    [Fact] // D5 — critical
    public async Task HT05_FailureAfterPersistence_RetryDoesNotDuplicate()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.FailAfterFirstOrderInsert);
        var first = await customer.SubmitCheckoutAsync(form);
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        RequireSeam(host, "failure after persistence");
        Assert.True(first.ServerError, "The injected failure did not reach the customer; scenario invalid.");
        await customer.SubmitCheckoutAsync(form);
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));
    }

    [Fact] // D1, D6 — critical
    public async Task HT06_LegitimateSubsequentPurchase_SameItems_CreatesSecondOrder()
    {
        var (host, customer, item) = await ArrangeAsync();
        await using var _h = host;
        Assert.True((await customer.SubmitCheckoutAsync(await customer.OpenCheckoutAsync())).LandedOnConfirmation, "First purchase did not complete.");
        await customer.AddItemAsync(item.Id, item.Name, item.Price);
        var second = await customer.SubmitCheckoutAsync(await customer.OpenCheckoutAsync());
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, await host.Db.OrderCountAsync(Demo));
        Assert.True(second.LandedOnConfirmation, "The legitimate second purchase did not reach the confirmation page.");
    }

    [Fact] // D6
    public async Task HT07_LegitimateSubsequentPurchase_DifferentItems_CreatesSecondOrder()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        Assert.True((await customer.SubmitCheckoutAsync(await customer.OpenCheckoutAsync())).LandedOnConfirmation, "First purchase did not complete.");
        var other = await Item.SecondAsync(host);
        await customer.AddItemAsync(other.Id, other.Name, other.Price);
        await customer.SubmitCheckoutAsync(await customer.OpenCheckoutAsync());
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, await host.Db.OrderCountAsync(Demo));
    }

    [Fact] // D7
    public async Task HT08_RetryFromFreshCheckoutPageWhileFirstInFlight()
    {
        if (D7ExpectedOrdersForFreshPageRetry is not { } expected)
        {
            Assert.Skip("PENDING-DECISION: D7 (is a resubmission from a reloaded checkout page a retry or a new purchase?)");
            return;
        }
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.DelayFirstOrderInsert, TimeSpan.FromSeconds(3));
        var first = customer.SubmitCheckoutAsync(form);
        await Task.Delay(500);
        var fresh = await customer.OpenCheckoutAsync();
        await customer.SubmitCheckoutAsync(fresh);
        await first;
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(expected, await host.Db.OrderCountAsync(Demo));
    }

    [Fact] // existing behaviour — critical
    public async Task HT09_Compatibility_CheckoutHappyPath()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var result = await customer.SubmitCheckoutAsync(await customer.OpenCheckoutAsync());
        Assert.True(result.LandedOnConfirmation, $"Checkout did not land on the confirmation page (status {result.Status}, path {result.FinalPath}).");
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));
        Assert.Equal(1, await host.Db.OrderItemUnitsAsync(Demo));
        Assert.Equal(0, await host.Db.BasketItemCountAsync(Demo));
    }

    [Fact] // existing behaviour
    public async Task HT10_Compatibility_EmptyBasketCheckout_CreatesNoOrder()
    {
        await using var host = await EShopHost.StartAsync();
        var customer = new ShopClient(host, Demo);
        await customer.LoginAsync();
        var result = await customer.SubmitEmptyCheckoutAsync();
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await host.Db.OrderCountAsync(Demo));
        Assert.False(result.ServerError, "Empty-basket checkout produced a server error.");
    }

    [Fact] // protection must not cross customers
    public async Task HT11_DifferentCustomersCheckingOutConcurrently_EachGetsOneOrder()
    {
        await using var host = await EShopHost.StartAsync();
        var item = await Item.FirstAsync(host);
        var a = new ShopClient(host, Demo);
        var b = new ShopClient(host, Other);
        foreach (var c in new[] { a, b }) { await c.LoginAsync(); await c.AddItemAsync(item.Id, item.Name, item.Price); }
        var fa = await a.OpenCheckoutAsync();
        var fb = await b.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.DelayFirstOrderInsert, TimeSpan.FromSeconds(1));
        await Task.WhenAll(a.SubmitCheckoutAsync(fa), b.SubmitCheckoutAsync(fb));
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));
        Assert.Equal(1, await host.Db.OrderCountAsync(Other));
    }

    [Fact] // D2
    public async Task HT12_RetryOfCompletedCheckout_ShowsConfirmation()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        Assert.True((await customer.SubmitCheckoutAsync(form)).LandedOnConfirmation, "First checkout did not complete.");
        var retry = await customer.SubmitCheckoutAsync(form);
        Assert.False(retry.ServerError, "Retry of a completed checkout produced a server error.");
        Assert.True(retry.LandedOnConfirmation, $"Retry of a completed checkout did not show the order confirmation (landed on {retry.FinalPath}).");
    }

    [Fact] // D3
    public async Task HT13_ConcurrentDuplicateSubmissions_NoServerError()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.DelayFirstOrderInsert, TimeSpan.FromSeconds(2));
        var a = customer.SubmitCheckoutAsync(form);
        await Task.Delay(300);
        var b = customer.SubmitCheckoutAsync(form);
        var results = await Task.WhenAll(a, b);
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        Assert.All(results, r => Assert.False(r.ServerError, "A duplicate submission ended in a server error."));
    }
}

public sealed record Item(int Id, string Name, decimal Price)
{
    public static Task<Item> FirstAsync(EShopHost host) => NthAsync(host, 0);
    public static Task<Item> SecondAsync(EShopHost host) => NthAsync(host, 1);

    private static async Task<Item> NthAsync(EShopHost host, int n)
    {
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(host.CatalogConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT Id, Name, Price FROM Catalog ORDER BY Id OFFSET {n} ROWS FETCH NEXT 1 ROWS ONLY";
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), "Catalog is empty.");
        return new Item(r.GetInt32(0), r.GetString(1), r.GetDecimal(2));
    }
}
