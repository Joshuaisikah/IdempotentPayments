using IdempotentPayments.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IdempotentPayments.Infrastructure.Data;

public class IdempotencyStore
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;

    private readonly IdempotencyOptions _options;

    public IdempotencyStore(AppDbContext db, TimeProvider time, IdempotencyOptions options)
    {
        _db = db;
        _time = time;
        _options = options;
    }

    public Task<IdempotencyKey?> FindAsync(string key, CancellationToken ct) =>
        _db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Key == key, ct);

    public async Task<bool> TryClaimAsync(
        string key,
        string requestHash,
        Guid paymentId,
        CancellationToken ct
    )
    {
        var now = _time.GetUtcNow().UtcDateTime;
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

    // WHERE guarantees: only the caller whose ClaimedAt still matches wins, and only while no response is stored.
    public async Task<bool> TryTakeOverAsync(
        string key,
        DateTime seenClaimedAt,
        CancellationToken ct
    )
    {
        var now = _time.GetUtcNow().UtcDateTime;
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

    // WHERE guarantees: one winner (CreatedAt must still be the value we read), and only a finished key is reclaimed.
    public async Task<bool> TryReclaimExpiredAsync(
        string key,
        DateTime seenCreatedAt,
        string newHash,
        Guid newPaymentId,
        CancellationToken ct
    )
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var rows = await _db
            .IdempotencyKeys.Where(k =>
                k.Key == key && k.CreatedAt == seenCreatedAt && k.ResponseBody != null
            )
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(k => k.RequestHash, newHash)
                        .SetProperty(k => k.PaymentId, newPaymentId)
                        .SetProperty(k => k.CreatedAt, now)
                        .SetProperty(k => k.ClaimedAt, now)
                        .SetProperty(k => k.ResponseStatusCode, (int?)null)
                        .SetProperty(k => k.ResponseBody, (string?)null),
                ct
            );
        return rows == 1;
    }

    // Housekeeping only. The read check in ResolveAsync is the real rule.
    public async Task<int> DeleteExpiredAsync(int batchSize, CancellationToken ct)
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - _options.KeyLifetime;
        var keys = await _db
            .IdempotencyKeys.Where(k => k.ResponseBody != null && k.CreatedAt < cutoff)
            .OrderBy(k => k.CreatedAt)
            .Select(k => k.Key)
            .Take(batchSize)
            .ToListAsync(ct);

        // WHERE is re-checked at delete time, so a row re-claimed a moment ago survives.
        return await _db
            .IdempotencyKeys.Where(k =>
                keys.Contains(k.Key) && k.ResponseBody != null && k.CreatedAt < cutoff
            )
            .ExecuteDeleteAsync(ct);
    }
}
