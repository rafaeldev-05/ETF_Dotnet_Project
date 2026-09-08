using Etf.Application;
using Microsoft.EntityFrameworkCore;

namespace Etf.Infrastructure;

public sealed class PostgresOutboxStore(TradingDb db) : IOutboxStore
{
    public async Task<IReadOnlyList<OutboxMessageView>> ClaimPending(int batchSize, TimeSpan retryBackoff, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var eligibleBefore = DateTimeOffset.UtcNow - retryBackoff;
        var rows = await db.OutboxMessages.Where(x => x.Status == OutboxStatus.Pending)
            .Where(x => x.LastAttemptAt == null || x.LastAttemptAt <= eligibleBefore)
            .OrderBy(x => x.CreatedAt).Take(batchSize).ToListAsync(ct);
        foreach (var row in rows) { row.Attempts++; row.LastAttemptAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Select(ToView).ToArray();
    }
    public async Task MarkPublished(Guid eventId, CancellationToken ct)
    {
        var row = await db.OutboxMessages.SingleAsync(x => x.EventId == eventId, ct);
        row.Status = OutboxStatus.Published; row.Error = null;
        await db.SaveChangesAsync(ct);
    }
    public async Task MarkPublishFailure(Guid eventId, string error, int maxAttempts, CancellationToken ct)
    {
        var row = await db.OutboxMessages.SingleAsync(x => x.EventId == eventId, ct);
        row.Error = error; row.Status = row.Attempts >= maxAttempts ? OutboxStatus.DeadLetter : OutboxStatus.Pending;
        await db.SaveChangesAsync(ct);
    }
    private static OutboxMessageView ToView(OutboxMessage x) => new(x.EventId, x.OrderId, x.ClientId, x.Type,
        x.ContractVersion, x.Payload, x.CreatedAt, x.Status.ToString(), x.Attempts, x.LastAttemptAt, x.Error);
}
