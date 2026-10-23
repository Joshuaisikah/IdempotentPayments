using System.Text.Json;
using IdempotentPayments.Api.DTOs;
using IdempotentPayments.Infrastructure.Data;
using IdempotentPayments.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace IdempotentPayments.Api.Endpoints;

public static class PaymentEndpoints
{
    private const decimal MaxAmount = 1_000_000m;
    private const int RetryAfterSeconds = 5;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/payments");
        group.MapPost("/", CreateAsync);
        group.MapGet("{id:guid}", GetAsync);
        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreatePaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext http,
        PaymentService service,
        IdempotencyStore store,
        IdempotencyOptions options,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 255)
            return Results.BadRequest(
                new { error = "Idempotency-Key header is required (max 255 characters)." }
            );
        if (request.Amount <= 0 || request.Amount > MaxAmount)
            return Results.BadRequest(
                new { error = $"Amount must be greater than 0 and at most {MaxAmount}." }
            );
        if (decimal.Round(request.Amount, 2) != request.Amount)
            return Results.BadRequest(new { error = "Amount can have at most 2 decimal places." });
        if (
            string.IsNullOrWhiteSpace(request.Currency)
            || request.Currency.Length != 3
            || !request.Currency.All(char.IsAsciiLetter)
        )
            return Results.BadRequest(new { error = "Currency must be a 3-letter code." });

        var currency = request.Currency.ToUpperInvariant();
        var hash = RequestFingerprint.Compute(request.Amount, currency);

        var existing = await store.FindAsync(idempotencyKey, ct);
        if (existing is not null)
            return await ResolveAsync(
                http,
                existing,
                hash,
                request.Amount,
                currency,
                service,
                store,
                options,
                ct
            );

        var paymentId = Guid.NewGuid();
        var claimed = await store.TryClaimAsync(idempotencyKey, hash, paymentId, ct);
        if (!claimed)
        {
            existing = await store.FindAsync(idempotencyKey, ct);
            if (existing is null)
                return InProgress(http);
            return await ResolveAsync(
                http,
                existing,
                hash,
                request.Amount,
                currency,
                service,
                store,
                options,
                ct
            );
        }

        try
        {
            var payment = await service.CreateAsync(paymentId, request.Amount, currency, ct);
            var body = JsonSerializer.Serialize(ToResponse(payment), JsonOptions);
            await store.CompleteAsync(idempotencyKey, StatusCodes.Status201Created, body);
            return Replay(http, StatusCodes.Status201Created, body, payment.Id);
        }
        // ProviderTimeout fired (outcome unknown) → 503. Client disconnect → rethrow.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ServiceUnavailable(http);
        }
    }

    private static async Task<IResult> ResolveAsync(
        HttpContext http,
        IdempotencyKey existing,
        string hash,
        decimal amount,
        string currency,
        PaymentService service,
        IdempotencyStore store,
        IdempotencyOptions options,
        CancellationToken ct
    )
    {
        if (existing.RequestHash != hash)
            return Results.UnprocessableEntity(
                new { error = "This Idempotency-Key was already used with a different request." }
            );

        if (existing.ResponseBody is not null && existing.ResponseStatusCode is not null)
            return Replay(
                http,
                existing.ResponseStatusCode.Value,
                existing.ResponseBody,
                existing.PaymentId
            );

        var age = DateTime.UtcNow - existing.ClaimedAt;
        if (age < options.StaleAfter || existing.PaymentId is null)
            return InProgress(http);

        if (!await store.TryTakeOverAsync(existing.Key, existing.ClaimedAt, ct))
            return InProgress(http);

        try
        {
            var payment = await service.ResumeAsync(existing.PaymentId.Value, amount, currency, ct);
            var body = JsonSerializer.Serialize(ToResponse(payment), JsonOptions);
            await store.CompleteAsync(existing.Key, StatusCodes.Status201Created, body);
            return Replay(http, StatusCodes.Status201Created, body, payment.Id);
        }
        catch (OwnershipLostException)
        {
            // X is owned by someone else and not settled: ask the client to retry.
            return InProgress(http);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ServiceUnavailable(http);
        }
    }

    private static IResult InProgress(HttpContext http)
    {
        http.Response.Headers["Retry-After"] = RetryAfterSeconds.ToString();
        return Results.Conflict(
            new { error = "A request with this Idempotency-Key is still in progress." }
        );
    }

    private static IResult ServiceUnavailable(HttpContext http)
    {
        http.Response.Headers["Retry-After"] = RetryAfterSeconds.ToString();
        return Results.Json(
            new { error = "Payment provider timed out; outcome is unknown. Retry later." },
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }

    private static IResult Replay(HttpContext http, int statusCode, string body, Guid? paymentId)
    {
        if (paymentId is not null)
            http.Response.Headers.Location = $"/api/payments/{paymentId}";
        return Results.Content(body, "application/json", statusCode: statusCode);
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

    internal static PaymentResponse ToResponse(Payment p) =>
        new(p.Id, p.Amount, p.Currency, p.Status.ToString(), p.ProviderReference, p.CreatedAt);
}
