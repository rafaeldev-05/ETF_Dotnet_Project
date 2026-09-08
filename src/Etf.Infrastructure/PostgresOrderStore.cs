using System.Data;
using System.Text.Json;
using Etf.Application;
using Etf.Domain;
using Microsoft.EntityFrameworkCore;
namespace Etf.Infrastructure;

public sealed class PostgresOrderStore(TradingDb db) : IOrderStore
{
    public async Task<OrderView> ReserveAndCreate(Order candidate, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var account = await LockAccount(candidate.ClientId, ct)
            ?? throw new RequestFailure(401, "unknown_client", "Conta de demonstração desconhecida.");
        var previous = await db.Orders.Include(x => x.History)
            .SingleOrDefaultAsync(x => x.ClientId == candidate.ClientId && x.IdempotencyKey == candidate.IdempotencyKey, ct);
        if (previous is not null)
        {
            if (!previous.HasSameRequest(candidate))
                throw new RequestFailure(409, "idempotency_conflict", "Chave já usada com outro conteúdo.");
            await tx.CommitAsync(ct);
            return OrderView.From(previous);
        }
        if (!await db.Funds.AnyAsync(x => x.Symbol == candidate.Etf, ct))
            throw new RequestFailure(404, "etf_not_found", "ETF não encontrado.");
        if (!account.TryReserve(candidate.Reservation))
            throw new RequestFailure(409, "insufficient_balance", "Saldo disponível insuficiente.");
        db.Orders.Add(candidate);
        var eventId = Guid.NewGuid();
        var occurred = candidate.CreatedAt;
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventId = eventId, OrderId = candidate.Id, ClientId = candidate.ClientId,
            Type = "OrderCreated", ContractVersion = 1,
            Payload = JsonSerializer.Serialize(new OrderCreatedV1(eventId, candidate.Id, candidate.ClientId,
                candidate.Etf, candidate.Quantity, candidate.LimitPrice, candidate.Reservation, occurred)),
            CreatedAt = occurred, Status = OutboxStatus.Pending
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return OrderView.From(candidate);
    }

    public async Task<OrderView> ApplyResult(Guid clientId, Guid id, SimulationOutcome outcome, decimal? price, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // Global lock order: account first, then order. Creation also locks the account first.
        var account = await LockAccount(clientId, ct) ?? throw NotFound();
        var order = await db.Orders.FromSqlInterpolated($"SELECT * FROM \"Orders\" WHERE \"Id\" = {id} AND \"ClientId\" = {clientId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw NotFound();
        await db.Entry(order).Collection(x => x.History).LoadAsync(ct);
        switch (outcome)
        {
            case SimulationOutcome.Executed:
                if (order.Execute(price!.Value))
                    account.ConsumeReservation(order.Reservation, order.ExecutedAmount!.Value);
                break;
            case SimulationOutcome.Rejected:
                if (order.Reject(ProcessOrderResult.DemoRejectionReason))
                    account.ReleaseReservation(order.Reservation);
                break;
            case SimulationOutcome.TemporaryFailure:
                // A timeout provides no definitive execution result. No financial/state mutation.
                if (order.Status != OrderStatus.PendingExecution) throw new OrderResultConflict();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return OrderView.From(order);
    }

    public async Task<bool> ConsumeCreated(OrderCreatedV1 message, SimulationOutcome outcome, decimal? price, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var account = await LockAccount(message.ClientId, ct) ?? throw NotFound();
        var order = await db.Orders.FromSqlInterpolated($"SELECT * FROM \"Orders\" WHERE \"Id\" = {message.OrderId} AND \"ClientId\" = {message.ClientId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw NotFound();
        await db.Entry(order).Collection(x => x.History).LoadAsync(ct);
        var existing = await db.ProcessedEvents.SingleOrDefaultAsync(x => x.EventId == message.EventId, ct);
        if (existing is not null) { await tx.CommitAsync(ct); return false; }
        switch (outcome)
        {
            case SimulationOutcome.Executed:
                if (order.Execute(price!.Value)) account.ConsumeReservation(order.Reservation, order.ExecutedAmount!.Value);
                break;
            case SimulationOutcome.Rejected:
                if (order.Reject(ProcessOrderResult.DemoRejectionReason)) account.ReleaseReservation(order.Reservation);
                break;
            case SimulationOutcome.TemporaryFailure:
                throw new InvalidOperationException("temporary consumer failure");
        }
        db.ProcessedEvents.Add(new ProcessedEvent { EventId = message.EventId, OrderId = message.OrderId, ProcessedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private Task<Account?> LockAccount(Guid id, CancellationToken ct) => db.Accounts
        .FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
    private static RequestFailure NotFound() => new(404, "order_not_found", "Ordem não encontrada.");

    public async Task<OrderView?> Find(Guid clientId, Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.History)
            .SingleOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct);
        return order is null ? null : OrderView.From(order);
    }
    public async Task<IReadOnlyList<Fund>> Catalog(CancellationToken ct) => await db.Funds.AsNoTracking().OrderBy(x => x.Symbol).ToListAsync(ct);
}
