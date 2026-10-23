namespace IdempotentPayments.Infrastructure.Data;

public class IdempotencyKey
{
    public required string Key { get; set; }
    public required string RequestHash { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public Guid? PaymentId { get; set; }
    public DateTime CreatedAt { get; set; }
}
