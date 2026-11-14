using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.eShopWeb.Infrastructure.Data;
using Microsoft.eShopWeb.Web.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CheckoutRetry.HiddenTests.Infrastructure;

/// <summary>
/// Hosts the implementation under test exactly as the application configures itself in Development, but against
/// fresh, uniquely named SQL Server LocalDB databases (production provider: real transactions, locks and unique
/// indexes). The application's own startup migrates and seeds them. The only additions are a fault-injecting
/// EF Core interceptor and an outer middleware used to simulate timeouts/failures. No internal design is assumed.
/// </summary>
public sealed class EShopHost : WebApplicationFactory<IBasketViewModelService>
{
    public const string LocalDbServer = @"(localdb)\mssqllocaldb";
    private readonly string _catalogDb = $"HiddenEval_Catalog_{Guid.NewGuid():N}";
    private readonly string _identityDb = $"HiddenEval_Identity_{Guid.NewGuid():N}";

    public FaultInjector Faults { get; } = new();
    public ErrorLogCapture Errors { get; } = new();

    public string CatalogConnectionString => ConnectionString(_catalogDb);
    private static string ConnectionString(string db) =>
        $"Server={LocalDbServer};Integrated Security=true;Initial Catalog={db};TrustServerCertificate=true;Connect Timeout=60";

    public static async Task<EShopHost> StartAsync()
    {
        await EnsureLocalDbAvailableAsync();
        var host = new EShopHost();
        // Environment variables are read by WebApplication.CreateBuilder before any registration code runs.
        Environment.SetEnvironmentVariable("ConnectionStrings__CatalogConnection", host.CatalogConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__IdentityConnection", ConnectionString(host._identityDb));
        Environment.SetEnvironmentVariable("UseOnlyInMemoryDatabase", "false");
        _ = host.Server; // force startup (migration + seeding) now
        await host.WaitForSeedAsync();
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.AddProvider(Errors));
        builder.UseSetting("ConnectionStrings:CatalogConnection", CatalogConnectionString);
        builder.UseSetting("ConnectionStrings:IdentityConnection", ConnectionString(_identityDb));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Faults);
            // Composes with whatever the application configured (EF Core IDbContextOptionsConfiguration).
            services.ConfigureDbContext<CatalogContext>((sp, options) => options.AddInterceptors(Faults.Interceptor, Faults.Transactions));
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new FaultMiddlewareStartupFilter(Faults));
        });
    }

    private static async Task EnsureLocalDbAvailableAsync()
    {
        try
        {
            await using var c = new SqlConnection($"Server={LocalDbServer};Integrated Security=true;Initial Catalog=master;TrustServerCertificate=true;Connect Timeout=60");
            await c.OpenAsync();
        }
        catch (Exception ex)
        {
            Assert.Fail($"INDETERMINATE: infrastructure — SQL Server LocalDB unavailable: {ex.Message}");
        }
    }

    private async Task WaitForSeedAsync()
    {
        // Seeding runs during startup; confirm the catalog is queryable so failures surface clearly.
        string? last = null;
        for (var i = 0; i < 30; i++)
        {
            try
            {
                if (await Db.ScalarAsync<int>("SELECT COUNT(*) FROM Catalog") > 0) return;
            }
            catch (SqlException ex) { last = ex.Message; }
            await Task.Delay(500);
        }
        Assert.Fail("Application failed to create/seed its database on startup (migrations or seeding failed in the implementation under test). "
            + $"Last query error: {last}. Logged errors: {Errors.Summary()}");
    }

    public DbQueries Db => new(CatalogConnectionString);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        foreach (var db in new[] { _catalogDb, _identityDb })
        {
            try
            {
                await using var c = new SqlConnection($"Server={LocalDbServer};Integrated Security=true;Initial Catalog=master;TrustServerCertificate=true");
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
                await cmd.ExecuteNonQueryAsync();
            }
            catch { /* best effort cleanup */ }
        }
    }
}

/// <summary>Read-only black-box queries against persisted state (table names from the original schema).</summary>
public sealed class DbQueries(string connectionString)
{
    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        var result = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    public Task<int> OrderCountAsync(string buyerId) =>
        ScalarAsync<int>("SELECT COUNT(*) FROM Orders WHERE BuyerId = @b", ("@b", buyerId));

    public Task<int> OrderItemUnitsAsync(string buyerId) =>
        ScalarAsync<int>("SELECT ISNULL(SUM(oi.Units),0) FROM OrderItems oi JOIN Orders o ON oi.OrderId = o.Id WHERE o.BuyerId = @b", ("@b", buyerId));

    public Task<int> BasketItemCountAsync(string buyerId) =>
        ScalarAsync<int>("SELECT COUNT(*) FROM BasketItems bi JOIN Baskets b ON bi.BasketId = b.Id WHERE b.BuyerId = @b", ("@b", buyerId));
}
