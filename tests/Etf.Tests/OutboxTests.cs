using System.Net;
using System.Text.Json;
using Etf.Application;
using Etf.Domain;
using Etf.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Etf.Tests;

public sealed partial class OrderTests
{
    private async Task<(Guid orderId, OutboxMessage message)> CreateWithOutbox(string key = "outbox")
    {
        var orderId = await CreatePending(key);
        await using var db = Context();
        var message = await db.OutboxMessages.SingleAsync(x => x.OrderId == orderId);
        Assert.Equal("OrderCreated", message.Type);
        Assert.Equal(1, message.ContractVersion);
        Assert.Equal(OutboxStatus.Pending, message.Status);
        return (orderId, message);
    }

    [Fact]
    public async Task Creating_order_creates_exactly_one_outbox_in_same_transaction()
    {
        var pair = await CreateWithOutbox();
        await using var db = Context();
        Assert.Equal(1, await db.Orders.CountAsync(x => x.Id == pair.orderId));
        Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.OrderId == pair.orderId));
        var eventBody = JsonSerializer.Deserialize<OrderCreatedV1>(pair.message.Payload)!;
        Assert.Equal(pair.message.EventId, eventBody.EventId);
        Assert.Equal(pair.orderId, eventBody.OrderId);
        Assert.Equal(DemoData.Alice, eventBody.ClientId);
    }

    [Fact]
    public async Task Failure_before_commit_leaves_order_reservation_and_outbox_absent()
    {
        await using (var db = Context(new FailBeforeCommit()))
        {
            var useCase = new Orders(new PostgresOrderStore(db));
            await Assert.ThrowsAsync<InjectedFailure>(() => useCase.Create(DemoData.Alice, "outbox-rollback",
                new CreateOrderRequest("DEMO11", 2, 100), default));
        }
        await AssertFunds(1000, 0, 0);
        await using var check = Context();
        Assert.Empty(await check.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Publisher_publishes_pending_message_and_marks_it_published()
    {
        var pair = await CreateWithOutbox();
        await using var db = Context();
        var broker = new InMemoryBroker();
        var publisher = new OutboxPublisher(new PostgresOutboxStore(db), broker);
        Assert.Equal(1, await publisher.PublishBatch(10, 3, default));
        Assert.Single(broker.Messages);
        var row = await db.OutboxMessages.SingleAsync(x => x.EventId == pair.message.EventId);
        Assert.Equal(OutboxStatus.Published, row.Status);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task Broker_failure_keeps_pending_and_increments_attempts_then_dead_letters()
    {
        var pair = await CreateWithOutbox();
        await using var db = Context();
        var broker = new InMemoryBroker { FailNext = true };
        var publisher = new OutboxPublisher(new PostgresOutboxStore(db), broker);
        await publisher.PublishBatch(10, 2, default);
        var first = await db.OutboxMessages.SingleAsync(x => x.EventId == pair.message.EventId);
        Assert.Equal(OutboxStatus.Pending, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Contains("broker failure", first.Error);
        broker.FailNext = true;
        await Task.Delay(1100);
        await publisher.PublishBatch(10, 2, default);
        var second = await db.OutboxMessages.SingleAsync(x => x.EventId == pair.message.EventId);
        Assert.Equal(OutboxStatus.DeadLetter, second.Status);
        Assert.Equal(2, second.Attempts);
    }

    [Fact]
    public async Task Publisher_reexecution_can_publish_again_without_corrupting_outbox()
    {
        var pair = await CreateWithOutbox();
        await using var db = Context();
        var broker = new InMemoryBroker();
        var publisher = new OutboxPublisher(new PostgresOutboxStore(db), broker);
        await publisher.PublishBatch(10, 3, default);
        // Simulate the crash window by putting the row back to Pending; a real crash
        // would leave it pending because MarkPublished had not committed.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxMessages\" SET \"Status\" = 'Pending' WHERE \"EventId\" = {pair.message.EventId}");
        await using var db2 = Context();
        var publisher2 = new OutboxPublisher(new PostgresOutboxStore(db2), broker);
        await Task.Delay(1100);
        await publisher2.PublishBatch(10, 3, default);
        Assert.Equal(2, broker.Messages.Count(x => x.EventId == pair.message.EventId));
        Assert.Equal(OutboxStatus.Published, await db2.OutboxMessages.Where(x => x.EventId == pair.message.EventId).Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task Consumer_processes_event_once_even_when_received_twice()
    {
        var pair = await CreateWithOutbox();
        var message = JsonSerializer.Deserialize<OrderCreatedV1>(pair.message.Payload)!;
        await using var db = Context();
        var consumer = new OrderConsumer(new PostgresOrderStore(db));
        Assert.True(await consumer.Consume(message, SimulationOutcome.Executed, 120, default));
        Assert.False(await consumer.Consume(message, SimulationOutcome.Executed, 120, default));
        await AssertFunds(760, 0);
        Assert.Equal(1, await db.ProcessedEvents.CountAsync(x => x.EventId == message.EventId));
    }

    [Fact]
    public async Task Concurrent_consumers_do_not_execute_or_move_balance_twice()
    {
        var pair = await CreateWithOutbox("consumer-concurrent");
        var message = JsonSerializer.Deserialize<OrderCreatedV1>(pair.message.Payload)!;
        var first = Task.Run(async () => { await using var db = Context(); return await new OrderConsumer(new PostgresOrderStore(db)).Consume(message, SimulationOutcome.Executed, 120, default); });
        var second = Task.Run(async () => { await using var db = Context(); return await new OrderConsumer(new PostgresOrderStore(db)).Consume(message, SimulationOutcome.Executed, 120, default); });
        var result = await Task.WhenAll(first, second);
        Assert.Equal(1, result.Count(x => x));
        Assert.Equal(1, result.Count(x => !x));
        await AssertFunds(760, 0);
    }

    [Fact]
    public async Task Temporary_consumer_failure_leaves_event_unprocessed_and_order_pending()
    {
        var pair = await CreateWithOutbox("consumer-retry");
        var message = JsonSerializer.Deserialize<OrderCreatedV1>(pair.message.Payload)!;
        await using var db = Context();
        var consumer = new OrderConsumer(new PostgresOrderStore(db));
        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Consume(message, SimulationOutcome.TemporaryFailure, null, default));
        await AssertOrder(pair.orderId, OrderStatus.PendingExecution);
        Assert.Equal(0, await db.ProcessedEvents.CountAsync());
        await AssertFunds(749, 251);
    }
}
