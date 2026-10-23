using IdempotentPayments.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IdempotentPayments.Infrastructure.Services;

public class PaymentService
{
    private readonly AppDbContext _db;
    private readonly IPaymentProvider _provider;
    private readonly IdempotencyOptions _options;

    public PaymentService(AppDbContext db, IPaymentProvider provider, IdempotencyOptions options)
    {
        _db = db;
        _provider = provider;
        _options = options;
    }

    public async Task<Payment> CreateAsync(
        Guid id,
        decimal amount,
        string currency,
        CancellationToken ct
    )
    {
        var now = DateTime.UtcNow;
        var payment = new Payment
        {
            Id = id,
            Amount = amount,
            Currency = currency,
            Status = PaymentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct); // Pending: safe to cancel
        return await ContinueAsync(payment, ct);
    }

    public async Task<Payment> ResumeAsync(
        Guid id,
        decimal amount,
        string currency,
        CancellationToken ct
    )
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);

        if (payment is null)
            return await CreateAsync(id, amount, currency, ct);

        try
        {
            switch (payment.Status)
            {
                case PaymentStatus.Succeeded:
                case PaymentStatus.Failed:
                    return payment;

                case PaymentStatus.Pending:
                    return await ContinueAsync(payment, ct);

                case PaymentStatus.Processing:
                    await TouchOwnedAsync(payment, ct);

                    var status = await WithTimeout(
                        t => _provider.GetStatusAsync(payment.Id.ToString(), t),
                        ct
                    );

                    return status.Outcome switch
                    {
                        ProviderOutcome.Charged or ProviderOutcome.Declined => await SettleAsync(
                            payment,
                            status
                        ),
                        ProviderOutcome.NotFound => await ChargeAndSettleAsync(payment, ct),
                        _ => throw new InvalidOperationException(
                            $"Unexpected provider outcome '{status.Outcome}' from GetStatusAsync."
                        ),
                    };

                default:
                    throw new InvalidOperationException($"Unhandled status '{payment.Status}'.");
            }
        }
        catch (OwnershipLostException)
        {
            // Someone else moved X. Re-read the database's values into the tracked object.
            await _db.Entry(payment).ReloadAsync(ct);

            // Settled by the sweep or another taker: take the existing "settled" branch.
            if (payment.Status is PaymentStatus.Succeeded or PaymentStatus.Failed)
                return payment;

            // Still in flight under someone else: let the endpoint answer 409.
            throw;
        }
    }

    private async Task<T> WithTimeout<T>(
        Func<CancellationToken, Task<T>> call,
        CancellationToken ct
    )
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.ProviderTimeout);
        return await call(cts.Token);
    }

    private async Task TouchOwnedAsync(Payment payment, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var updated = await _db
            .Payments.Where(p =>
                p.Id == payment.Id
                && p.Status == PaymentStatus.Processing
                && p.UpdatedAt == payment.UpdatedAt
            )
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(p => p.Status, PaymentStatus.Processing)
                        .SetProperty(p => p.UpdatedAt, now),
                ct
            );

        if (updated == 0)
            throw new OwnershipLostException(payment.Id);

        payment.UpdatedAt = now;
    }

    private async Task<Payment> ChargeAndSettleAsync(Payment payment, CancellationToken ct)
    {
        var result = await WithTimeout(
            t => _provider.ChargeAsync(payment.Id.ToString(), payment.Amount, payment.Currency, t),
            ct
        );
        return await SettleAsync(payment, result);
    }

    private async Task<Payment> ContinueAsync(Payment payment, CancellationToken ct)
    {
        var readStatus = payment.Status;
        var readUpdatedAt = payment.UpdatedAt;
        var now = DateTime.UtcNow;

        // Only one of {takeover, sweep} may move this row out of its current state.
        var updated = await _db
            .Payments.Where(p =>
                p.Id == payment.Id && p.Status == readStatus && p.UpdatedAt == readUpdatedAt
            )
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(p => p.Status, PaymentStatus.Processing)
                        .SetProperty(p => p.UpdatedAt, now),
                ct
            );

        if (updated == 0)
            throw new OwnershipLostException(payment.Id);

        payment.Status = PaymentStatus.Processing;
        payment.UpdatedAt = now;
        return await ChargeAndSettleAsync(payment, ct);
    }

    private async Task<Payment> SettleAsync(Payment payment, ProviderResult result)
    {
        switch (result.Outcome)
        {
            case ProviderOutcome.Charged:
                SetStatus(payment, PaymentStatus.Succeeded);
                payment.ProviderReference = result.Reference;
                break;

            case ProviderOutcome.Declined:
                SetStatus(payment, PaymentStatus.Failed);
                // ── ITEM 5: same FailureReason as the sweep path.
                payment.FailureReason = PaymentOutcomeMapper.FailureReasonFor(result.Outcome);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unexpected provider outcome '{result.Outcome}'."
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

public sealed class OwnershipLostException(Guid paymentId)
    : Exception($"Lost ownership of payment {paymentId}.");
