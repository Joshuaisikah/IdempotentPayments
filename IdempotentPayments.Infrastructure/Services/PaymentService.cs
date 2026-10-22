using IdempotentPayments.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IdempotentPayments.Infrastructure.Services;

public class PaymentService
{
    private readonly AppDbContext _db;
    private readonly IPaymentProvider _provider;

    public PaymentService(AppDbContext db, IPaymentProvider provider)
    {
        _db = db;
        _provider = provider;
    }

    public async Task<Payment> CreateAsync(decimal amount, string currency, CancellationToken ct)
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            Amount = amount,
            Currency = currency,
            Status = PaymentStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);
        SetStatus(payment, PaymentStatus.Processing);
        await _db.SaveChangesAsync(ct);
        var result = await _provider.ChargeAsync(
            payment.Id.ToString(),
            payment.Amount,
            payment.Currency,
            ct
        );
        switch (result.Outcome)
        {
            case ProviderOutcome.Charged:
                SetStatus(payment, PaymentStatus.Succeeded);
                payment.ProviderReference = result.Reference;
                break;
            case ProviderOutcome.Declined:
                SetStatus(payment, PaymentStatus.Failed);
                break;
            default:
                throw new InvalidOperationException(
                    $"Invalid outcome from '{result.Outcome}' from ChargeAsync."
                );
        }
        await _db.SaveChangesAsync(CancellationToken.None);
        return payment;
    }

    private static void SetStatus(Payment p, PaymentStatus status)
    {
        var now = DateTime.UtcNow;
        p.Status = status;
        p.UpdatedAt = now;
        if (status is PaymentStatus.Succeeded or PaymentStatus.Failed)
            p.CompletedAt = now;
    }

    public Task<Payment?> GetAsync(Guid id, CancellationToken ct) =>
        _db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);
}
