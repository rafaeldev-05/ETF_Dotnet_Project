using Etf.Domain;
using Microsoft.EntityFrameworkCore;
namespace Etf.Infrastructure;
public static class DemoData
{
    public static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static async Task Seed(TradingDb db)
    {
        // Explicit, repeatable Development-only seed. Never resets existing balances.
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Accounts\" (\"Id\", \"Available\", \"Reserved\") VALUES ({Alice}, 1000, 0), ({Bob}, 1000, 0) ON CONFLICT DO NOTHING");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"Funds\" (\"Symbol\", \"Name\") VALUES ('DEMO11', 'ETF fictício demonstração'), ('TEST11', 'ETF fictício teste') ON CONFLICT DO NOTHING");
    }
}
