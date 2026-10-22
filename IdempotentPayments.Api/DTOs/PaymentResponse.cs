namespace IdempotentPayments.Api.DTOs;

public record PaymentResponse(
    Guid Id,
    decimal Amount,
    string Currency,
    string Status,
    string? ProviderReference,
    DateTime CreatedAt
);
