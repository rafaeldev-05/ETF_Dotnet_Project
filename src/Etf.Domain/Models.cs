namespace Etf.Domain;

public sealed class Account
{
    public Guid Id { get; set; }
    public decimal Available { get; private set; }
    public decimal Reserved { get; private set; }

    public bool TryReserve(decimal amount)
    {
        if (!Money.ValidPrice(amount)) throw new ArgumentOutOfRangeException(nameof(amount));
        if (Available < amount) return false;
        Available -= amount;
        Reserved += amount;
        return true;
    }

    public void ConsumeReservation(decimal originalReservation, decimal executedAmount)
    {
        if (!Money.ValidPrice(executedAmount) || executedAmount > originalReservation)
            throw new ArgumentOutOfRangeException(nameof(executedAmount));
        ReleaseReservation(originalReservation, originalReservation - executedAmount);
    }

    public void ReleaseReservation(decimal originalReservation) => ReleaseReservation(originalReservation, originalReservation);

    private void ReleaseReservation(decimal originalReservation, decimal refund)
    {
        if (!Money.ValidPrice(originalReservation) || Reserved < originalReservation || Available > Money.MaxValue - refund)
            throw new InvalidOperationException("Reserva ou saldo inconsistente; operação não aplicada.");
        Reserved -= originalReservation;
        Available += refund;
    }
}

public sealed class Fund
{
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
}

public enum OrderStatus { PendingExecution, Executed, Rejected }
public sealed class InvalidOrderData(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
public sealed class OrderResultConflict() : Exception("A ordem já possui um resultado final diferente.");

public sealed class Order
{
    private readonly List<OrderTransition> _history = [];
    private Order() { } // EF materialization; creation by the application goes through CreatePending.
    public Guid Id { get; private set; }
    public Guid ClientId { get; private set; }
    public string Etf { get; private set; } = "";
    public int Quantity { get; private set; }
    public decimal LimitPrice { get; private set; }
    public decimal Reservation { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public OrderStatus Status { get; private set; } = OrderStatus.PendingExecution;
    public DateTimeOffset CreatedAt { get; private set; }
    public decimal? ExecutionPrice { get; private set; }
    public decimal? ExecutedAmount { get; private set; }
    public string? RejectionReason { get; private set; }
    public DateTimeOffset? FinalizedAt { get; private set; }
    public IReadOnlyCollection<OrderTransition> History => _history.AsReadOnly();

    public static Order CreatePending(Guid clientId, string? key, string? etf, int quantity, decimal limitPrice)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(c => c < 33 || c > 126))
            throw new InvalidOrderData("invalid_idempotency_key", "Idempotency-Key deve conter de 1 a 128 caracteres ASCII visíveis, sem espaços.");
        if (clientId == Guid.Empty || string.IsNullOrWhiteSpace(etf) || etf.Length > 16 || quantity <= 0 || !Money.ValidPrice(limitPrice))
            throw new InvalidOrderData("invalid_order", "Informe cliente, ETF, quantidade inteira positiva e preço positivo com até duas casas decimais.");
        if (limitPrice > Money.MaxValue / quantity)
            throw new InvalidOrderData("invalid_order", "Reserva excede a precisão monetária suportada.");
        var order = new Order { Id = Guid.NewGuid(), ClientId = clientId, Etf = etf.Trim().ToUpperInvariant(),
            Quantity = quantity, LimitPrice = limitPrice, Reservation = quantity * limitPrice,
            IdempotencyKey = key, CreatedAt = UtcNow() };
        order._history.Add(OrderTransition.Record(order.Id, null, OrderStatus.PendingExecution, order.CreatedAt));
        return order;
    }

    public bool HasSameRequest(Order other) => Etf == other.Etf && Quantity == other.Quantity && LimitPrice == other.LimitPrice;

    // Returns false for a semantic replay: no balance movement or additional transition.
    public bool Execute(decimal price)
    {
        if (Status != OrderStatus.PendingExecution)
        {
            if (Status == OrderStatus.Executed && ExecutionPrice == price) return false;
            throw new OrderResultConflict();
        }
        if (!Money.ValidPrice(price) || price > LimitPrice)
            throw new InvalidOrderData("invalid_execution_price", "Preço de execução deve ser positivo, ter até duas casas decimais e não exceder o limite.");
        ExecutionPrice = price;
        ExecutedAmount = Quantity * price;
        Finish(OrderStatus.Executed);
        return true;
    }

    public bool Reject(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 100)
            throw new InvalidOrderData("invalid_rejection_reason", "Motivo de rejeição inválido.");
        if (Status != OrderStatus.PendingExecution)
        {
            if (Status == OrderStatus.Rejected && RejectionReason == reason) return false;
            throw new OrderResultConflict();
        }
        RejectionReason = reason;
        Finish(OrderStatus.Rejected);
        return true;
    }

    private void Finish(OrderStatus status)
    {
        var previous = Status;
        Status = status;
        FinalizedAt = UtcNow();
        _history.Add(OrderTransition.Record(Id, previous, status, FinalizedAt.Value));
    }
    private static DateTimeOffset UtcNow() => new(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
}

public sealed class OrderTransition
{
    private OrderTransition() { }
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public OrderStatus? FromStatus { get; private set; }
    public OrderStatus ToStatus { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    internal static OrderTransition Record(Guid orderId, OrderStatus? from, OrderStatus to, DateTimeOffset at) =>
        new() { Id = Guid.NewGuid(), OrderId = orderId, FromStatus = from, ToStatus = to, OccurredAt = at };
}

public static class Money
{
    public const decimal MaxValue = 9999999999999999.99m;
    public static bool ValidPrice(decimal value) => value > 0 && value <= MaxValue && decimal.Round(value, 2) == value;
}
