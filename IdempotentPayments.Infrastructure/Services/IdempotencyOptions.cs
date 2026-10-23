namespace IdempotentPayments.Infrastructure.Services;

public record IdempotencyOptions(TimeSpan StaleAfter);
