using System.Collections.Concurrent;
using System.ComponentModel;

namespace IdempotentPayments.Infrastructure.Services;

public class FakePaymentProvider : IPaymentProvider
{
    public enum Behaviour
    {
        Success,
        Decline,
        TimeoutBeforeCharge,
        TimeoutAfterCharge,
    }

    private readonly ConcurrentDictionary<string, Behaviour> _behaviours = new();
    private readonly ConcurrentDictionary<string, ProviderResult> _charges = new();

    // null = throw for unconfigured references (what the tests rely on).
    public Behaviour? DefaultBehaviour { get; set; }

    public void Configure(string reference, Behaviour behaviour) =>
        _behaviours[reference] = behaviour;

    public Task<ProviderResult> ChargeAsync(
        string reference,
        decimal amount,
        string currency,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        if (!_behaviours.TryGetValue(reference, out var behaviour))
        {
            if (DefaultBehaviour is null)
            {
                throw new InvalidOperationException(
                    $"No behaviour configured for reference '{reference}'."
                );
            }
            behaviour = DefaultBehaviour.Value;
        }
        switch (behaviour)
        {
            case Behaviour.TimeoutBeforeCharge:
                return Task.FromException<ProviderResult>(
                    new TaskCanceledException("Timed out before the charge")
                );
            case Behaviour.TimeoutAfterCharge:
                Record(reference, ProviderOutcome.Charged);
                return Task.FromException<ProviderResult>(
                    new TaskCanceledException("Time out after the charge")
                );
            case Behaviour.Success:
                return Task.FromResult(Record(reference, ProviderOutcome.Charged));
            case Behaviour.Decline:
                return Task.FromResult(Record(reference, ProviderOutcome.Declined));
            default:
                throw new NotSupportedException($"Unhandled behaviour '{behaviour}'.");
        }
    }

    private ProviderResult Record(string reference, ProviderOutcome outcome) =>
        _charges.GetOrAdd(
            reference,
            _ => new ProviderResult(
                outcome,
                outcome == ProviderOutcome.Charged ? Guid.NewGuid().ToString("N") : null
            )
        );

    public Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = _charges.TryGetValue(reference, out var providerId)
            ? providerId
            : new ProviderResult(ProviderOutcome.NotFound, null);
        return Task.FromResult(result);
    }
}
