using IdempotentPayments.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IdempotentPayments.Tests.Integration;

// Test classes run in parallel; on a fresh payment_test database they would all
// run MigrateAsync at once and race to create the same tables. Migrate once per test run.
internal static class TestDatabase
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";

    private static readonly Lazy<Task> Migrated = new(async () =>
    {
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);
        await db.Database.MigrateAsync();
    });

    public static Task EnsureMigratedAsync() => Migrated.Value;
}
