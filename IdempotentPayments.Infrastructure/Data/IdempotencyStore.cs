using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IdempotentPayments.Infrastructure.Data;

public class IdempotencyStore
{
    private readonly AppDbContext _db;

    public IdempotencyStore(AppDbContext db) => _db = db;

    public Task<IdempotencyKey?> FindAsync(string key, CancellationToken ct) =>
        _db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Key == key, ct);

    public async Task<bool> TryClaimAsync(string key, string requestHash, CancellationToken ct)
    {
        _db.IdempotencyKeys.Add(
            new IdempotencyKey
            {
                Key = key,
                RequestHash = requestHash,
                CreatedAt = DateTime.UtcNow,
            }
        );

        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException
                    is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            )
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    public Task CompleteAsync(string key, int statusCode, string body, Guid paymentId) =>
        _db
            .IdempotencyKeys.Where(k => k.Key == key)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(k => k.ResponseStatusCode, statusCode)
                        .SetProperty(k => k.ResponseBody, body)
                        .SetProperty(k => k.PaymentId, paymentId),
                CancellationToken.None
            );
}
