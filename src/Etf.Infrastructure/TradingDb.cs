using Etf.Domain;
using Microsoft.EntityFrameworkCore;
namespace Etf.Infrastructure;

public sealed class TradingDb(DbContextOptions<TradingDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Fund> Funds => Set<Fund>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderTransition> OrderTransitions => Set<OrderTransition>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e => {
            e.ToTable("Accounts", t => { t.HasCheckConstraint("account_nonnegative", "\"Available\" >= 0 AND \"Reserved\" >= 0"); });
            e.HasKey(x => x.Id); e.Property(x => x.Available).HasPrecision(18, 2); e.Property(x => x.Reserved).HasPrecision(18, 2);
        });
        b.Entity<Fund>(e => { e.HasKey(x => x.Symbol); e.Property(x => x.Symbol).HasMaxLength(16); e.Property(x => x.Name).HasMaxLength(100); });
        b.Entity<Order>(e => {
            e.ToTable("Orders", t => t.HasCheckConstraint("order_valid", "\"Quantity\" > 0 AND \"LimitPrice\" > 0 AND \"Reservation\" = \"Quantity\" * \"LimitPrice\""));
            e.ToTable("Orders", t => t.HasCheckConstraint("order_result_valid", """
                ("Status" = 'PendingExecution' AND "ExecutionPrice" IS NULL AND "ExecutedAmount" IS NULL AND "RejectionReason" IS NULL AND "FinalizedAt" IS NULL)
                OR ("Status" = 'Executed' AND "ExecutionPrice" IS NOT NULL AND "ExecutionPrice" > 0 AND "ExecutionPrice" <= "LimitPrice"
                    AND "ExecutedAmount" IS NOT NULL AND "ExecutedAmount" = "Quantity" * "ExecutionPrice" AND "RejectionReason" IS NULL AND "FinalizedAt" IS NOT NULL)
                OR ("Status" = 'Rejected' AND "ExecutionPrice" IS NULL AND "ExecutedAmount" IS NULL AND "RejectionReason" IS NOT NULL
                    AND length("RejectionReason") > 0 AND "FinalizedAt" IS NOT NULL)
                """));
            e.Property(x => x.ExecutionPrice).HasPrecision(18, 2);
            e.Property(x => x.ExecutedAmount).HasPrecision(18, 2);
            e.Property(x => x.RejectionReason).HasMaxLength(100);
            e.HasMany(x => x.History).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.Navigation(x => x.History).HasField("_history").UsePropertyAccessMode(PropertyAccessMode.Field);
            e.HasKey(x => x.Id); e.Property(x => x.Etf).HasMaxLength(16); e.Property(x => x.IdempotencyKey).HasMaxLength(128);
            e.Property(x => x.LimitPrice).HasPrecision(18, 2); e.Property(x => x.Reservation).HasPrecision(18, 2);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.ClientId, x.IdempotencyKey }).IsUnique();
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Fund>().WithMany().HasForeignKey(x => x.Etf).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<OrderTransition>(e => {
            e.ToTable("OrderTransitions", t => t.HasCheckConstraint("transition_valid", """
                ("FromStatus" IS NULL AND "ToStatus" = 'PendingExecution')
                OR ("FromStatus" IS NOT NULL AND "FromStatus" = 'PendingExecution' AND "ToStatus" IN ('Executed', 'Rejected'))
                """));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.OrderId, x.ToStatus }).IsUnique();
            e.HasIndex(x => x.OrderId).IsUnique().HasFilter("\"ToStatus\" <> 'PendingExecution'");
        });
        b.Entity<OutboxMessage>(e => {
            e.HasKey(x => x.EventId);
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });
        b.Entity<ProcessedEvent>(e => {
            e.HasKey(x => x.EventId);
            e.HasIndex(x => x.OrderId);
        });
    }
}
