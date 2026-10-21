namespace IdempotentPayments.Infrastructure.Services;

public interface IPaymentProvider
{
    Task<ProviderResult> ChargeAsync(
        string reference,
        decimal amount,
        string currency,
        CancellationToken ct
    );
    Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct);
}
