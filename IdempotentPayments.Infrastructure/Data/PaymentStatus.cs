namespace IdempotentPayments.Infrastructure.Data;

public enum PaymentStatus
{
    Pending,
    Processing,
    Succeeded,
    Failed,
}
