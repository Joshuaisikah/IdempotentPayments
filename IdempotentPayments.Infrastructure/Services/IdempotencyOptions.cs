namespace IdempotentPayments.Infrastructure.Services;

public sealed class IdempotencyOptions
{
    private static readonly TimeSpan DbMargin = TimeSpan.FromSeconds(10);

    public TimeSpan StaleAfter { get; }

    public TimeSpan ProviderTimeout { get; }

    public IdempotencyOptions(TimeSpan staleAfter, TimeSpan providerTimeout)
    {
        var longestPath = (2 * providerTimeout) + DbMargin;
        if (staleAfter <= longestPath)
            throw new InvalidOperationException(
                $"StaleAfter ({staleAfter}) must be greater than 2 x ProviderTimeout + {DbMargin} ({longestPath})."
            );

        StaleAfter = staleAfter;
        ProviderTimeout = providerTimeout;
    }
}
