namespace IdempotentPayments.Infrastructure.Services;

public enum ProviderOutcome
{
    Charged,
    NotFound,
    Declined,
}
