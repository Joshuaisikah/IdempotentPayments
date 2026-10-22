using IdempotentPayments.Api.DTOs;
using IdempotentPayments.Infrastructure.Data;
using IdempotentPayments.Infrastructure.Services;

namespace IdempotentPayments.Api.Endpoints;

public static class PaymentEndpoints
{
    private const decimal MaxAmount = 1_000_000m;

    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/payments");
        group.MapPost("/", CreateAsync);
        group.MapGet("{id:guid}", GetAsync);
        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreatePaymentRequest request,
        PaymentService service,
        CancellationToken ct
    )
    {
        if (request.Amount <= 0 || request.Amount > MaxAmount)
            return Results.BadRequest(new { error = $"Amount must be between 1 and {MaxAmount}." });

        if (decimal.Round(request.Amount, 2) != request.Amount)
            return Results.BadRequest(new { error = "Amount can have at most 2 decimal places." });

        if (
            string.IsNullOrWhiteSpace(request.Currency)
            || request.Currency.Length != 3
            || !request.Currency.All(char.IsAsciiLetter)
        )
            return Results.BadRequest(new { error = "Currency must be a 3-letter code." });
        var payment = await service.CreateAsync(
            request.Amount,
            request.Currency.ToUpperInvariant(),
            ct
        );
        return Results.Created($"/api/payments/{payment.Id}", ToResponse(payment));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        PaymentService service,
        CancellationToken ct
    )
    {
        var payment = await service.GetAsync(id, ct);
        return payment is null ? Results.NotFound() : Results.Ok(ToResponse(payment));
    }

    private static PaymentResponse ToResponse(Payment p) =>
        new(p.Id, p.Amount, p.Currency, p.Status.ToString(), p.ProviderReference, p.CreatedAt);
}
