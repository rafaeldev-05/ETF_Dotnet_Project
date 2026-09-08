using Etf.Domain;

namespace Etf.Application;

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessageView>> ClaimPending(int batchSize, TimeSpan retryBackoff, CancellationToken ct);
    Task MarkPublished(Guid eventId, CancellationToken ct);
    Task MarkPublishFailure(Guid eventId, string error, int maxAttempts, CancellationToken ct);
}

public interface ILocalBroker
{
    Task Publish(OutboxMessageView message, CancellationToken ct);
}

public sealed class OutboxPublisher(IOutboxStore store, ILocalBroker broker)
{
    public async Task<int> PublishBatch(int batchSize, int maxAttempts, CancellationToken ct, TimeSpan? retryBackoff = null)
    {
        var messages = await store.ClaimPending(Math.Clamp(batchSize, 1, 100), retryBackoff ?? TimeSpan.FromSeconds(1), ct);
        foreach (var message in messages)
        {
            try
            {
                await broker.Publish(message, ct);
                await store.MarkPublished(message.EventId, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await store.MarkPublishFailure(message.EventId, error.Message, maxAttempts, ct);
            }
        }
        return messages.Count;
    }
}

public sealed class OrderConsumer(IOrderStore store)
{
    public async Task<bool> Consume(OrderCreatedV1 message, SimulationOutcome outcome, decimal? price, CancellationToken ct)
        => await store.ConsumeCreated(message, outcome, price, ct);
}

public sealed class InMemoryBroker : ILocalBroker
{
    private readonly List<OutboxMessageView> _messages = [];
    private readonly object _gate = new();
    public bool FailNext { get; set; }
    public IReadOnlyList<OutboxMessageView> Messages { get { lock (_gate) return _messages.ToArray(); } }
    public Task Publish(OutboxMessageView message, CancellationToken ct)
    {
        lock (_gate)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("demo broker failure"); }
            _messages.Add(message);
        }
        return Task.CompletedTask;
    }
}
