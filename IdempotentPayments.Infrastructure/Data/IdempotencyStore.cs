using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IdempotentPayments.Infrastructure.Data;

public class IdempotencyStore
{
    private readonly AppDbContext _db;

    public IdempotencyStore(AppDbContext db) => _db = db;

    public Task<IdempotencyKey?> FindAsync(string key, CancellationToken ct) =>
        _db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Key == key, ct);

    public async Task<bool> TryClaimAsync(
        string key,
        string requestHash,
        Guid paymentId,
        CancellationToken ct
    )
    {
        var now = DateTime.UtcNow;
        _db.IdempotencyKeys.Add(
            new IdempotencyKey
            {
                Key = key,
                RequestHash = requestHash,
                PaymentId = paymentId,
                CreatedAt = now,
                ClaimedAt = now,
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

    public Task CompleteAsync(string key, int statusCode, string body) =>
        _db
            .IdempotencyKeys.Where(k => k.Key == key)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(k => k.ResponseStatusCode, statusCode)
                        .SetProperty(k => k.ResponseBody, body),
                CancellationToken.None
            );

    public async Task<bool> TryTakeOverAsync(
        string key,
        DateTime seenClaimedAt,
        CancellationToken ct
    )
    {
        var now = DateTime.UtcNow;
        var rows = await _db
            .IdempotencyKeys.Where(k =>
                k.Key == key && k.ClaimedAt == seenClaimedAt && k.ResponseBody == null
            )
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.ClaimedAt, now), ct);
        return rows == 1;
    }

    public async Task<bool> TryCompleteIfPendingAsync(Guid paymentId, int statusCode, string body)
    {
        var rows = await _db
            .IdempotencyKeys.Where(k => k.PaymentId == paymentId && k.ResponseBody == null)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(k => k.ResponseStatusCode, statusCode)
                        .SetProperty(k => k.ResponseBody, body),
                CancellationToken.None
            );
        return rows == 1;
    }
}
