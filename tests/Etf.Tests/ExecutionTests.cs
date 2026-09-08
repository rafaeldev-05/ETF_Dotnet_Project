using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Etf.Application;
using Etf.Domain;
using Etf.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Etf.Tests;

public sealed partial class OrderTests
{
    private async Task<Guid> CreatePending(string key = "execution", decimal limit = 125.50m)
    {
        var response = await Post(key: key, quantity: 2, price: limit);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Simulate(Guid id, string outcome = "Executed", decimal? price = 120m, Guid? owner = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/development/orders/{id}/result")
        {
            Content = JsonContent.Create(new SimulationRequest(outcome, price))
        };
        request.Headers.Add("X-Demo-Client-Id", (owner ?? DemoData.Alice).ToString());
        return client.SendAsync(request);
    }

    private async Task AssertFunds(decimal available, decimal reserved, int orderCount = 1)
    {
        await using var db = Context();
        var account = await db.Accounts.SingleAsync(x => x.Id == DemoData.Alice);
        var consumed = await db.Orders.Where(x => x.ClientId == DemoData.Alice).SumAsync(x => x.ExecutedAmount ?? 0);
        Assert.Equal(available, account.Available);
        Assert.Equal(reserved, account.Reserved);
        Assert.Equal(1000m, account.Available + account.Reserved + consumed);
        Assert.Equal(orderCount, await db.Orders.CountAsync(x => x.ClientId == DemoData.Alice));
    }

    private async Task AssertOrder(Guid id, OrderStatus status, decimal? price = null, decimal? amount = null, decimal limit = 125.50m)
    {
        await using var db = Context();
        var order = await db.Orders.AsNoTracking().Include(x => x.History).SingleAsync(x => x.Id == id);
        Assert.Equal(status, order.Status);
        Assert.Equal(price, order.ExecutionPrice);
        Assert.Equal(amount, order.ExecutedAmount);
        Assert.Equal(2, order.Quantity);
        Assert.Equal(limit, order.LimitPrice);
        Assert.Equal(2 * limit, order.Reservation); // Original reservation never overwritten.
        Assert.Equal("DEMO11", order.Etf);
        Assert.Equal(DemoData.Alice, order.ClientId);
        var creation = Assert.Single(order.History, h => h.FromStatus is null);
        Assert.Equal(OrderStatus.PendingExecution, creation.ToStatus);
        Assert.Equal(order.CreatedAt, creation.OccurredAt);
        if (status == OrderStatus.PendingExecution)
        {
            Assert.Null(order.FinalizedAt);
            Assert.Null(order.RejectionReason);
            Assert.Single(order.History);
        }
        else
        {
            Assert.Equal(2, order.History.Count);
            var final = Assert.Single(order.History, h => h.FromStatus.HasValue);
            Assert.Equal(OrderStatus.PendingExecution, final.FromStatus);
            Assert.Equal(status, final.ToStatus);
            Assert.Equal(order.FinalizedAt, final.OccurredAt);
            Assert.Equal(status == OrderStatus.Rejected ? ProcessOrderResult.DemoRejectionReason : null, order.RejectionReason);
        }
    }

    [Fact]
    public async Task Executes_at_limit_and_consumes_full_reservation()
    {
        var id = await CreatePending();
        var response = await Simulate(id, price: 125.50m);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertOrder(id, OrderStatus.Executed, 125.50m, 251m);
        await AssertFunds(749m, 0);
        var body = (await Json(response)).GetProperty("order");
        var get = await Get($"/orders/{id}", DemoData.Alice);
        Assert.True(JsonElement.DeepEquals(body, await Json(get)));
    }

    [Fact]
    public async Task Executes_below_limit_and_refunds_difference()
    {
        var id = await CreatePending();
        Assert.Equal(HttpStatusCode.OK, (await Simulate(id)).StatusCode);
        await AssertFunds(760, 0);
        await AssertOrder(id, OrderStatus.Executed, 120, 240);
    }

    [Fact]
    public async Task Rejects_and_returns_entire_reservation()
    {
        var id = await CreatePending();
        Assert.Equal(HttpStatusCode.OK, (await Simulate(id, "Rejected", null)).StatusCode);
        await AssertOrder(id, OrderStatus.Rejected);
        await AssertFunds(1000, 0);
    }

    [Fact]
    public async Task Temporary_failure_preserves_reservation_and_allows_later_success()
    {
        var id = await CreatePending();
        for (var i = 0; i < 2; i++)
        {
            var response = await Simulate(id, "TemporaryFailure", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("TemporaryFailure", (await Json(response)).GetProperty("outcome").GetString());
        }
        await AssertOrder(id, OrderStatus.PendingExecution);
        await AssertFunds(749, 251);
        Assert.Equal(HttpStatusCode.OK, (await Simulate(id)).StatusCode);
        await AssertFunds(760, 0);
    }

    [Theory]
    [InlineData("Executed")]
    [InlineData("Rejected")]
    public async Task Repeating_final_result_is_a_no_op(string outcome)
    {
        var id = await CreatePending();
        decimal? price = outcome == "Executed" ? 120m : null;
        var first = await Simulate(id, outcome, price);
        var firstJson = await Json(first);
        var replay = await Simulate(id, outcome, price);
        var replayJson = await Json(replay);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(firstJson, replayJson));
        await AssertFunds(outcome == "Executed" ? 760 : 1000, 0);
        await AssertOrder(id, outcome == "Executed" ? OrderStatus.Executed : OrderStatus.Rejected, price, price * 2);
        var creationReplay = await Post(key: "execution", quantity: 2, price: 125.50m);
        Assert.Equal(HttpStatusCode.Created, creationReplay.StatusCode);
        var creationJson = await Json(creationReplay);
        Assert.Equal(id, creationJson.GetProperty("id").GetGuid());
        Assert.Equal(outcome, creationJson.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(key: "execution", quantity: 3)).StatusCode);
        await AssertFunds(outcome == "Executed" ? 760 : 1000, 0);
    }

    [Theory]
    [InlineData("Executed", "Rejected", null)]
    [InlineData("Rejected", "Executed", 120.0)]
    [InlineData("Executed", "Executed", 121.0)]
    [InlineData("Executed", "TemporaryFailure", null)]
    public async Task Conflicting_result_never_overwrites_final_state(string first, string second, double? newPrice)
    {
        var id = await CreatePending();
        await Simulate(id, first, first == "Executed" ? 120m : null);
        var response = await Simulate(id, second, newPrice.HasValue ? (decimal)newPrice : null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("order_result_conflict", (await Json(response)).GetProperty("code").GetString());
        await AssertOrder(id, first == "Executed" ? OrderStatus.Executed : OrderStatus.Rejected,
            first == "Executed" ? 120m : null, first == "Executed" ? 240m : null);
        await AssertFunds(first == "Executed" ? 760 : 1000, 0);
    }

    [Theory]
    [InlineData("Executed", null)]
    [InlineData("Executed", 0.0)]
    [InlineData("Executed", -1.0)]
    [InlineData("Executed", 125.51)]
    [InlineData("Executed", 120.001)]
    [InlineData("Rejected", 120.0)]
    [InlineData("TemporaryFailure", 120.0)]
    [InlineData("unknown", null)]
    public async Task Invalid_result_does_not_change_state_or_balance(string outcome, double? price)
    {
        var id = await CreatePending();
        var response = await Simulate(id, outcome, price.HasValue ? (decimal)price : null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertFunds(749, 251);
        await AssertOrder(id, OrderStatus.PendingExecution);
    }

    [Fact]
    public async Task Another_owner_and_unknown_order_cannot_be_processed()
    {
        var id = await CreatePending();
        Assert.Equal(HttpStatusCode.NotFound, (await Simulate(id, owner: DemoData.Bob)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Simulate(Guid.NewGuid())).StatusCode);
        using var missingIdentity = await client.PostAsJsonAsync($"/development/orders/{id}/result", new SimulationRequest("Executed", 120));
        Assert.Equal(HttpStatusCode.Unauthorized, missingIdentity.StatusCode);
        await AssertFunds(749, 251);
        await AssertOrder(id, OrderStatus.PendingExecution);
    }

    [Fact]
    public async Task Concurrent_identical_results_move_money_and_append_history_once()
    {
        var id = await CreatePending();
        var responses = await Race(_ => Simulate(id), 12);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var first = await Json(responses[0]);
        var responseJson = new List<JsonElement>();
        foreach (var response in responses) responseJson.Add(await Json(response));
        foreach (var value in responseJson) Assert.True(JsonElement.DeepEquals(first, value));
        await AssertOrder(id, OrderStatus.Executed, 120, 240);
        await AssertFunds(760, 0);
    }

    [Fact]
    public async Task Concurrent_execution_and_rejection_have_one_winner()
    {
        var id = await CreatePending();
        var responses = await Race(i => i == 0 ? Simulate(id) : Simulate(id, "Rejected", null), 2);
        var success = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var executed = (await Json(success)).GetProperty("outcome").GetString() == "Executed";
        await AssertOrder(id, executed ? OrderStatus.Executed : OrderStatus.Rejected, executed ? 120m : null, executed ? 240m : null);
        await AssertFunds(executed ? 760 : 1000, 0);
    }

    [Fact]
    public async Task Concurrent_different_execution_prices_have_one_winner()
    {
        var id = await CreatePending();
        var responses = await Race(i => Simulate(id, price: 120 + i), 2);
        var success = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var price = (await Json(success)).GetProperty("order").GetProperty("executionPrice").GetDecimal();
        await AssertOrder(id, OrderStatus.Executed, price, price * 2);
        await AssertFunds(1000 - price * 2, 0);
    }

    [Fact]
    public async Task Two_orders_of_same_account_can_finalize_concurrently()
    {
        var first = await CreatePending();
        var second = await CreatePending("second", 100);
        var responses = await Race(i => i == 0 ? Simulate(first) : Simulate(second, price: 90), 2);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await AssertOrder(first, OrderStatus.Executed, 120, 240);
        await AssertOrder(second, OrderStatus.Executed, 90, 180, 100);
        await AssertFunds(580, 0, 2);
    }

    [Fact]
    public async Task Creation_and_finalization_share_account_lock_without_lost_update()
    {
        var id = await CreatePending();
        var responses = await Race(i => i == 0 ? Simulate(id) : Post(key: "new", quantity: 1, price: 700), 2);
        Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.Created, responses[1].StatusCode);
        await AssertFunds(60, 700, 2);
        await AssertOrder(id, OrderStatus.Executed, 120, 240);
    }

    [Theory]
    [InlineData("Executed")]
    [InlineData("Rejected")]
    public async Task Rollback_after_result_save_restores_balance_state_and_history(string outcome)
    {
        var id = await CreatePending();
        var before = await Json(await Get($"/orders/{id}", DemoData.Alice));
        await using (var db = Context(new FailBeforeCommit()))
        {
            var process = new ProcessOrderResult(new PostgresOrderStore(db));
            await Assert.ThrowsAsync<InjectedFailure>(() => process.Apply(DemoData.Alice, id,
                new SimulationRequest(outcome, outcome == "Executed" ? 120m : null), default));
        }
        await AssertFunds(749, 251);
        await AssertOrder(id, OrderStatus.PendingExecution);
        Assert.True(JsonElement.DeepEquals(before, await Json(await Get($"/orders/{id}", DemoData.Alice))));
        Assert.Equal(HttpStatusCode.OK, (await Simulate(id, outcome, outcome == "Executed" ? 120m : null)).StatusCode);
    }

    [Fact]
    public async Task Migration_preserves_legacy_order_balance_and_backfills_creation_history()
    {
        // Only this test's disposable database is downgraded to emulate an existing stage-1 database.
        var id = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 9, 8, 16, 55, 44, TimeSpan.Zero);
        await using (var db = Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260908164102_InitialOrders");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Orders" ("Id", "ClientId", "Etf", "Quantity", "LimitPrice", "Reservation", "IdempotencyKey", "Status", "CreatedAt")
                VALUES ({id}, {DemoData.Alice}, 'DEMO11', 2, 125.50, 251, 'legacy', 'PendingExecution', {created})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Accounts\" SET \"Available\" = 749, \"Reserved\" = 251 WHERE \"Id\" = {DemoData.Alice}");
            await db.Database.MigrateAsync();
        }
        await AssertOrder(id, OrderStatus.PendingExecution);
        await AssertFunds(749, 251);
        await using (var db = Context())
        {
            var order = await db.Orders.SingleAsync(x => x.Id == id);
            Assert.Equal(created, order.CreatedAt);
            Assert.Equal("legacy", order.IdempotencyKey);
        }
        Assert.Equal(HttpStatusCode.OK, (await Simulate(id)).StatusCode);
        await AssertOrder(id, OrderStatus.Executed, 120, 240);
        await AssertFunds(760, 0);
    }
}
