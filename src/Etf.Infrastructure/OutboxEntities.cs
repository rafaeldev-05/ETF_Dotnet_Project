namespace Etf.Infrastructure;

public enum OutboxStatus { Pending, Published, DeadLetter }

public sealed class OutboxMessage
{
    public Guid EventId { get; set; }
    public Guid OrderId { get; set; }
    public Guid ClientId { get; set; }
    public string Type { get; set; } = "";
    public int ContractVersion { get; set; }
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public OutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? Error { get; set; }
}

public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public Guid OrderId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
