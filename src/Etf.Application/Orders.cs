using Etf.Domain;
namespace Etf.Application;

public sealed record CreateOrderRequest(string? Etf, int Quantity, decimal LimitPrice);
public sealed record TransitionView(OrderStatus? FromStatus, OrderStatus ToStatus, DateTimeOffset OccurredAt);
public sealed record OrderView(Guid Id, string Etf, int Quantity, decimal LimitPrice,
    decimal Reservation, OrderStatus Status, DateTimeOffset CreatedAt, decimal? ExecutionPrice,
    decimal? ExecutedAmount, string? RejectionReason, DateTimeOffset? FinalizedAt, IReadOnlyList<TransitionView> History)
{
    public static OrderView From(Order o) => new(o.Id, o.Etf, o.Quantity, o.LimitPrice, o.Reservation, o.Status, o.CreatedAt,
        o.ExecutionPrice, o.ExecutedAmount, o.RejectionReason, o.FinalizedAt,
        o.History.OrderBy(x => x.FromStatus.HasValue).ThenBy(x => x.OccurredAt)
            .Select(x => new TransitionView(x.FromStatus, x.ToStatus, x.OccurredAt)).ToArray());
}
public sealed class RequestFailure(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
// Concrete atomic operations, without exposing EF transactions to the use cases.
public interface IOrderStore
{
    Task<OrderView> ReserveAndCreate(Order candidate, CancellationToken ct);
    Task<OrderView> ApplyResult(Guid clientId, Guid id, SimulationOutcome outcome, decimal? price, CancellationToken ct);
    Task<OrderView?> Find(Guid clientId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<Fund>> Catalog(CancellationToken ct);
}
public sealed class Orders(IOrderStore store)
{
    public Task<OrderView> Create(Guid clientId, string? key, CreateOrderRequest request, CancellationToken ct)
    {
        try
        {
            return store.ReserveAndCreate(Order.CreatePending(clientId, key, request.Etf, request.Quantity, request.LimitPrice), ct);
        }
        catch (InvalidOrderData e) { throw new RequestFailure(400, e.Code, e.Message); }
    }
}
