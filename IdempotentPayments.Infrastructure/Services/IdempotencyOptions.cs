namespace IdempotentPayments.Infrastructure.Services;

public sealed class IdempotencyOptions
{
    private static readonly TimeSpan DbMargin = TimeSpan.FromSeconds(10);

    public TimeSpan StaleAfter { get; }
    public TimeSpan ProviderTimeout { get; }

    // How long after CreatedAt a key is forgotten (M5).
    public TimeSpan KeyLifetime { get; }

    public IdempotencyOptions(
        TimeSpan staleAfter,
        TimeSpan providerTimeout,
        TimeSpan? keyLifetime = null
    )
    {
        var longestPath = (2 * providerTimeout) + DbMargin;
        if (staleAfter <= longestPath)
            throw new InvalidOperationException(
                $"StaleAfter ({staleAfter}) must be greater than 2 x ProviderTimeout + {DbMargin} ({longestPath})."
            );

        var lifetime = keyLifetime ?? TimeSpan.FromHours(24);
        // If a key could expire before it can even be called stale, a live request would lose its key.
        if (lifetime <= staleAfter)
            throw new InvalidOperationException(
                $"KeyLifetime ({lifetime}) must be greater than StaleAfter ({staleAfter})."
            );

        StaleAfter = staleAfter;
        ProviderTimeout = providerTimeout;
        KeyLifetime = lifetime;
    }
}
