using System.Text.Json;
using IdempotentPayments.Api.DTOs;
using IdempotentPayments.Api.Endpoints; // ITEM 4: ToResponse / JsonOptions
using IdempotentPayments.Infrastructure.Data;
using IdempotentPayments.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace IdempotentPayments.Api.Services;

public sealed class PaymentSweep
{
    private readonly AppDbContext _db;
    private readonly IPaymentProvider _provider;
    private readonly IdempotencyStore _store;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<PaymentSweep> _log;

    private readonly TimeProvider _time;

    public PaymentSweep(
        AppDbContext db,
        IPaymentProvider provider,
        IdempotencyStore store,
        IdempotencyOptions options,
        TimeProvider time,
        ILogger<PaymentSweep> log
    )
    {
        _db = db;
        _provider = provider;
        _store = store;
        _options = options;
        _time = time;
        _log = log;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - _options.StaleAfter;

        var stuck = await _db
            .Payments.Where(p =>
                (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Processing)
                && p.UpdatedAt < cutoff
            )
            .ToListAsync(ct);

        var settled = 0;

        foreach (var payment in stuck)
        {
            var readStatus = payment.Status;
            var readUpdatedAt = payment.UpdatedAt;

            ProviderResult? result = null;

            if (readStatus == PaymentStatus.Processing)
            {
                try
                {
                    result = await WithTimeout(
                        t => _provider.GetStatusAsync(payment.Id.ToString(), t),
                        ct
                    );
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // host shutting down
                }
                catch (OperationCanceledException)
                {
                    _log.LogWarning("Sweep: GetStatusAsync timed out for {PaymentId}", payment.Id);
                    continue;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Sweep: GetStatusAsync failed for {PaymentId}", payment.Id);
                    continue;
                }
            }

            var completedAt = _time.GetUtcNow().UtcDateTime;
            PaymentStatus newStatus;
            string? newFailureReason;
            string? newReference;

            if (result is not null)
            {
                newStatus = result.Outcome switch
                {
                    ProviderOutcome.Charged => PaymentStatus.Succeeded,
                    ProviderOutcome.Declined => PaymentStatus.Failed,
                    ProviderOutcome.NotFound => PaymentStatus.Failed,
                    _ => throw new InvalidOperationException(
                        $"Unexpected provider outcome '{result.Outcome}' in sweep."
                    ),
                };
                newFailureReason = PaymentOutcomeMapper.FailureReasonFor(result.Outcome);
                newReference =
                    result.Outcome == ProviderOutcome.Charged
                        ? result.Reference
                        : payment.ProviderReference;
            }
            else
            {
                // Pending: provider never called.
                newStatus = PaymentStatus.Failed;
                newFailureReason = SweepMarkers.NeverSent;
                newReference = payment.ProviderReference;
            }

            var updated = await _db
                .Payments.Where(p =>
                    p.Id == payment.Id && p.Status == readStatus && p.UpdatedAt == readUpdatedAt
                )
                .ExecuteUpdateAsync(
                    s =>
                        s.SetProperty(p => p.Status, newStatus)
                            .SetProperty(p => p.UpdatedAt, completedAt)
                            .SetProperty(p => p.CompletedAt, completedAt)
                            .SetProperty(p => p.ProviderReference, newReference)
                            .SetProperty(p => p.FailureReason, newFailureReason),
                    ct
                );

            if (updated == 0)
                continue; // live request won the race; it owns the settle

            var settledPayment = new Payment
            {
                Id = payment.Id,
                Amount = payment.Amount,
                Currency = payment.Currency,
                Status = newStatus,
                ProviderReference = newReference,
                FailureReason = newFailureReason,
                CreatedAt = payment.CreatedAt,
                UpdatedAt = completedAt,
                CompletedAt = completedAt,
            };

            var body = JsonSerializer.Serialize(
                PaymentEndpoints.ToResponse(settledPayment),
                PaymentEndpoints.JsonOptions
            );

            await _store.TryCompleteIfPendingAsync(
                settledPayment.Id,
                StatusCodes.Status201Created,
                body
            );

            settled++;
        }

        if (settled > 0)
            _log.LogInformation("Sweep settled {Count} stuck payment(s).", settled);

        var deleted = await _store.DeleteExpiredAsync(100, ct);
        if (deleted > 0)
            _log.LogInformation("Sweep deleted {Count} expired key(s).", deleted);
        return settled;
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
}
