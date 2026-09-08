using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Etf.Application;
using Etf.Domain;
using Etf.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Etf.Tests;

// Each test gets a separate real PostgreSQL database; never touches the demo tables.
public sealed partial class OrderTests : IAsyncLifetime
{
    private readonly string database = "etf_test_" + Guid.NewGuid().ToString("N");
    private readonly string admin = Environment.GetEnvironmentVariable("ETF_TEST_ADMIN")
        ?? "Host=localhost;Port=55432;Database=postgres;Username=etf;Password=etf_demo_only";
    private string connection = "";
    private ApiFactory factory = null!;
    private HttpClient client = null!;
    public async Task InitializeAsync()
    {
        await using var db = new NpgsqlConnection(admin);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", db);
        await command.ExecuteNonQueryAsync();
        connection = new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString;
        await using var context = Context();
        await context.Database.MigrateAsync();
        await DemoData.Seed(context);
        factory = new ApiFactory(connection);
        client = factory.CreateClient();
    }
    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var db = new NpgsqlConnection(admin);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", db);
        await command.ExecuteNonQueryAsync();
    }
    private TradingDb Context(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<TradingDb>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new TradingDb(options.Options);
    }
    private Task<HttpResponseMessage> Post(string key = "key-1", int quantity = 2, decimal price = 100,
        string etf = "DEMO11", Guid? owner = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/orders") {
            Content = JsonContent.Create(new CreateOrderRequest(etf, quantity, price)) };
        message.Headers.Add("X-Demo-Client-Id", (owner ?? DemoData.Alice).ToString());
        if (key.Length > 0) message.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(message);
    }
    private async Task<HttpResponseMessage> Get(string path, Guid owner)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.Add("X-Demo-Client-Id", owner.ToString());
        return await client.SendAsync(message);
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    private async Task AssertBalance(decimal available, decimal reserved, int count)
    {
        await using var db = Context();
        var account = await db.Accounts.SingleAsync(a => a.Id == DemoData.Alice);
        Assert.Equal(available, account.Available);
        Assert.Equal(reserved, account.Reserved);
        Assert.Equal(1000, account.Available + account.Reserved);
        Assert.Equal(count, await db.Orders.CountAsync(o => o.ClientId == DemoData.Alice));
    }
    [Fact]
    public async Task Creates_pending_order_reserves_and_exposes_location()
    {
        var catalog = await client.GetFromJsonAsync<JsonElement>("/etfs");
        Assert.Equal(2, catalog.GetArrayLength());
        var response = await Post();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("PendingExecution", body.GetProperty("status").GetString());
        Assert.Equal(200, body.GetProperty("reservation").GetDecimal());
        Assert.Equal($"/orders/{body.GetProperty("id").GetString()}", response.Headers.Location!.ToString());
        var retrieved = await Get(response.Headers.Location.ToString(), DemoData.Alice);
        Assert.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        Assert.True(JsonElement.DeepEquals(body, await Json(retrieved)));
        await AssertBalance(800, 200, 1);
    }
    [Fact]
    public async Task Insufficient_balance_does_not_reserve_or_create()
    {
        var response = await Post(quantity: 11);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("insufficient_balance", (await Json(response)).GetProperty("code").GetString());
        await AssertBalance(1000, 0, 0);
    }
    [Fact]
    public async Task Replay_returns_same_order_even_after_balance_is_exhausted()
    {
        var first = await Post(quantity: 10);
        var replay = await Post(quantity: 10, price: 100.00m, etf: " demo11 ");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.True(JsonElement.DeepEquals(await Json(first), await Json(replay)));
        await AssertBalance(0, 1000, 1);
    }
    [Theory]
    [InlineData("TEST11", 2, 100)]
    [InlineData("DEMO11", 3, 100)]
    [InlineData("DEMO11", 2, 101)]
    public async Task Different_content_conflicts(string etf, int quantity, decimal price)
    {
        await Post();
        var response = await Post(etf: etf, quantity: quantity, price: price);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("idempotency_conflict", (await Json(response)).GetProperty("code").GetString());
        await AssertBalance(800, 200, 1);
    }
    [Fact]
    public async Task Another_client_cannot_read_order_and_can_reuse_key()
    {
        var first = await Post();
        Assert.Equal(HttpStatusCode.NotFound, (await Get(first.Headers.Location!.ToString(), DemoData.Bob)).StatusCode);
        var second = await Post(owner: DemoData.Bob);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.NotEqual(first.Headers.Location, second.Headers.Location);
    }
    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 1.001)]
    [InlineData(2147483647, 9999999999999999.99)]
    public async Task Invalid_values_are_rejected(int quantity, decimal price)
    {
        var response = await Post(quantity: quantity, price: price);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        await AssertBalance(1000, 0, 0);
    }
    [Fact]
    public async Task Unknown_etf_and_missing_key_are_consistent_errors()
    {
        var unknown = await Post(etf: "UNKNOWN");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("etf_not_found", (await Json(unknown)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(key: "")).StatusCode);
        await AssertBalance(1000, 0, 0);
        // Rejected requests do not consume their key.
        Assert.Equal(HttpStatusCode.Created, (await Post()).StatusCode);
    }
    [Fact]
    public async Task Missing_identity_is_unauthorized()
    {
        var result = await client.GetAsync($"/orders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }
    [Fact]
    public async Task Malformed_body_returns_problem_details()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/orders") {
            Content = new StringContent("{ \"quantity\": 1.5 }", System.Text.Encoding.UTF8, "application/json") };
        message.Headers.Add("X-Demo-Client-Id", DemoData.Alice.ToString());
        message.Headers.Add("Idempotency-Key", "malformed");
        var result = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("application/problem+json", result.Content.Headers.ContentType!.MediaType);
        await AssertBalance(1000, 0, 0);
    }
    // Hold the account externally until at least two HTTP transactions are demonstrably
    // waiting on PostgreSQL locks. This proves real overlap, not just Task.WhenAll.
    private async Task<HttpResponseMessage[]> Race(Func<int, Task<HttpResponseMessage>> send, int count)
    {
        await using var gate = new NpgsqlConnection(connection);
        await gate.OpenAsync();
        await using var tx = await gate.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT * FROM \"Accounts\" WHERE \"Id\" = @id FOR UPDATE", gate, tx);
        command.Parameters.AddWithValue("id", DemoData.Alice);
        await command.ExecuteNonQueryAsync();
        var pending = Enumerable.Range(0, count).Select(send).ToArray();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                await using var check = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", gate, tx);
                if (Convert.ToInt32(await check.ExecuteScalarAsync(timeout.Token)) >= 2) break;
                // Refresh statistics snapshot while retaining the gate transaction.
                await using var clear = new NpgsqlCommand("SELECT pg_stat_clear_snapshot()", gate, tx);
                await clear.ExecuteNonQueryAsync(timeout.Token);
                await Task.Delay(25, timeout.Token);
            }
        }
        finally { await tx.RollbackAsync(); }
        return await Task.WhenAll(pending);
    }
    [Fact]
    public async Task Concurrent_different_keys_never_overspend()
    {
        var responses = await Race(i => Post(key: $"race-{i}", quantity: 1, price: 300), 12);
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        await AssertBalance(100, 900, 3);
    }
    [Fact]
    public async Task Concurrent_same_key_reserves_once()
    {
        var responses = await Race(_ => Post(), 12);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(responses.Select(r => r.Headers.Location).Distinct());
        await AssertBalance(800, 200, 1);
    }
    [Fact]
    public async Task Concurrent_conflicting_content_has_one_winner()
    {
        var responses = await Race(i => Post(quantity: i + 1, price: 10), 8);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var winner = await Json(responses.Single(r => r.StatusCode == HttpStatusCode.Created));
        var amount = winner.GetProperty("reservation").GetDecimal();
        await AssertBalance(1000 - amount, amount, 1);
    }
    [Fact]
    public async Task Failure_after_save_before_commit_rolls_back_balance_and_order()
    {
        await using (var db = Context(new FailBeforeCommit()))
        {
            var useCase = new Orders(new PostgresOrderStore(db));
            await Assert.ThrowsAsync<InjectedFailure>(() => useCase.Create(DemoData.Alice, "atomicity", new("DEMO11", 2, 100), default));
        }
        await AssertBalance(1000, 0, 0);
        Assert.Equal(HttpStatusCode.Created, (await Post(key: "atomicity")).StatusCode);
        await AssertBalance(800, 200, 1);
    }
    [Fact]
    public void Production_refuses_demo_identity_mechanism()
    {
        using var production = new ApiFactory(connection, "Production");
        var error = Assert.Throws<InvalidOperationException>(() => production.CreateClient());
        Assert.Contains("Development", error.Message);
    }
    private sealed class InjectedFailure : Exception;
    private sealed class FailBeforeCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw new InjectedFailure();
    }
    private sealed class ApiFactory(string connection, string environment = "Development") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment(environment).UseSetting("ConnectionStrings:Trading", connection);
    }
}
