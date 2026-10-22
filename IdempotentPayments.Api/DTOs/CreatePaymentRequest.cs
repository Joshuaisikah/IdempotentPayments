namespace IdempotentPayments.Api.DTOs;

public record CreatePaymentRequest(decimal Amount, string? Currency);
