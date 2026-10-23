namespace IdempotentPayments.Infrastructure.Data;

public class IdempotencyKey
{
    public required string Key { get; set; }
    public required string RequestHash { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public Guid? PaymentId { get; set; }

    // CreatedAt stays: when the key was first used (M4 expiry)

    public DateTime CreatedAt { get; set; }

    // when someone last took ownership (staleness)

    public DateTime ClaimedAt { get; set; }
}
