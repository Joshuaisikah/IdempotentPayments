namespace IdempotentPayments.Infrastructure.Services;

public static class PaymentOutcomeMapper
{
    public static string? FailureReasonFor(ProviderOutcome outcome) =>
        outcome switch
        {
            ProviderOutcome.Declined => "declined",
            ProviderOutcome.NotFound => "swept: provider NotFound",
            _ => null,
        };
}

public static class SweepMarkers
{
    public const string NeverSent = "swept: never sent";
}
