using Microsoft.EntityFrameworkCore;

namespace IdempotentPayments.Infrastructure.Data;

public class IdempotencyStore
{
    private readonly AppDbContext _db;

    public IdempotencyStore(AppDbContext db)
    {
        _db = db;
    }

    public Task<IdempotencyKey?> FindAsync(string key, CancellationToken ct) =>
        _db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Key == key, ct);

    public async Task SaveAsync(
        string key,
        string requestHash,
        int statusCode,
        string responseBody,
        Guid? paymentId
    )
    {
        _db.IdempotencyKeys.Add(
            new IdempotencyKey
            {
                Key = key,
                RequestHash = requestHash,
                ResponseStatusCode = statusCode,
                ResponseBody = responseBody,
                PaymentId = paymentId,
                CreatedAt = DateTime.UtcNow,
            }
        );

        await _db.SaveChangesAsync(CancellationToken.None);
    }
}
