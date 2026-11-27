using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

public sealed class BaselineTests
{
    [Fact]
    public async Task CreatingReservationDecrementsAvailableStock()
    {
        await using var api = await InventoryApi.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        using var created = await api.Client.PostAsJsonAsync("/reservations", new { value = 3 }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(7, (await api.Client.GetFromJsonAsync<JsonElement>("/stock", ct)).GetProperty("available").GetInt32());
    }
}
