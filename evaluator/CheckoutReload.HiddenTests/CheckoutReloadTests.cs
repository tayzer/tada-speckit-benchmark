using CheckoutReload.HiddenTests.Infrastructure;
using Xunit;

namespace CheckoutReload.HiddenTests;

/// <summary>
/// DRAFT hidden behavioural tests for experiment checkout-reload: "customers see an error page when they reload the
/// checkout page, or pay from two tabs, while the first payment is still processing". This experiment continues
/// each workflow's checkout-retry run, so every implementation already has duplicate-checkout protection.
/// Expectations map to the owner-approved decisions in experiments/checkout-retry/decisions.json (D1, D3, D6, D7).
/// Tests observe only HTTP behaviour and persisted rows, never internal types, so any reasonable design can pass.
/// Outcome conventions as in CheckoutRetry.HiddenTests: assertion failure = FAIL; "INDETERMINATE:" = the scenario
/// could not be exercised against this implementation.
/// </summary>
public sealed class CheckoutReloadTests
{
    private const string Demo = "demouser@microsoft.com";

    private static async Task<(EShopHost Host, ShopClient Customer, Item Item)> ArrangeAsync()
    {
        var host = await EShopHost.StartAsync();
        var customer = new ShopClient(host, Demo);
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

    private static void NoServerError(SubmitResult r, string which) =>
        Assert.False(r.ServerError, $"{which} ended in a server error (status {r.Status}, path {r.FinalPath}).");

    [Fact] // D7, D3 — the reported symptom — critical
    public async Task RL01_ReloadWhileFirstInFlight_OneOrder_NoServerError()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.DelayFirstOrderInsert, TimeSpan.FromSeconds(3));
        var first = customer.SubmitCheckoutAsync(form);
        await Task.Delay(500);
        var reloaded = await customer.SubmitCheckoutAsync(await customer.OpenCheckoutAsync());
        var firstResult = await first;
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        RequireSeam(host, "reload while the first submission is in flight");
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));
        NoServerError(firstResult, "The first submission");
        NoServerError(reloaded, "The submission from the reloaded checkout page");
    }

    [Fact] // D3 (two tabs), D7 — the reported symptom
    public async Task RL02_TwoTabsOpenedBeforePaying_BothSubmitted_OneOrder_NoServerError()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var tabA = await customer.OpenCheckoutAsync();
        var tabB = await customer.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.DelayFirstOrderInsert, TimeSpan.FromSeconds(3));
        var a = customer.SubmitCheckoutAsync(tabA);
        await Task.Delay(500);
        var b = await customer.SubmitCheckoutAsync(tabB);
        var aResult = await a;
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        RequireSeam(host, "two tabs");
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));
        NoServerError(aResult, "The submission from the first tab");
        NoServerError(b, "The submission from the second tab");
    }

    [Fact] // D3, D7: the customer gives up waiting, reloads the checkout page and pays again
    public async Task RL03_TimeoutThenReloadAndPayAgain_OneOrder_NoServerError()
    {
        var (host, customer, _) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.DelayFirstOrderInsert, TimeSpan.FromSeconds(4));
        // TestServer does not abandon a request on client cancellation, so the timeout is modelled by not waiting for
        // the first response. The server keeps processing it.
        var first = customer.SubmitCheckoutAsync(form);
        if (await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(1))) == first)
            Assert.Fail("INDETERMINATE: the first attempt completed before the simulated client timeout; scenario not exercised.");
        var reloaded = await customer.SubmitCheckoutAsync(await customer.OpenCheckoutAsync());
        var firstResult = await first;
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        RequireSeam(host, "timeout then reload");
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));
        NoServerError(firstResult, "The first submission");
        NoServerError(reloaded, "The submission from the reloaded checkout page");
    }

    [Fact] // D1, D6: the fix must not block a legitimate later purchase — critical
    public async Task RL04_AfterReloadRace_NewBasketIsANewPurchase()
    {
        var (host, customer, item) = await ArrangeAsync();
        await using var _h = host;
        var form = await customer.OpenCheckoutAsync();
        host.Faults.Arm(FaultMode.DelayFirstOrderInsert, TimeSpan.FromSeconds(2));
        var first = customer.SubmitCheckoutAsync(form);
        await Task.Delay(500);
        await customer.SubmitCheckoutAsync(await customer.OpenCheckoutAsync());
        await first;
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, await host.Db.OrderCountAsync(Demo));

        await customer.AddItemAsync(item.Id, item.Name, item.Price);
        var second = await customer.SubmitCheckoutAsync(await customer.OpenCheckoutAsync());
        await host.Faults.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, await host.Db.OrderCountAsync(Demo));
        Assert.True(second.LandedOnConfirmation, $"The later purchase did not reach the confirmation page (status {second.Status}, path {second.FinalPath}).");
    }
}

public sealed record Item(int Id, string Name, decimal Price)
{
    public static Task<Item> FirstAsync(EShopHost host) => NthAsync(host, 0);

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
