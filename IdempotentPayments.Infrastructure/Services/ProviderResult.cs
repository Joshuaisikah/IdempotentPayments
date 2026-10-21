namespace IdempotentPayments.Infrastructure.Services;

public record ProviderResult(ProviderOutcome Outcome, string? Reference);
