using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using IdempotentPayments.Infrastructure.Data;
using IdempotentPayments.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IdempotentPayments.Tests.Integration;

// M4 step 2: a retry that finds a STALE claim with no response takes over.
// Each test plants a "request A died here" state directly in the database,
// then sends B (a single, sequential retry) and checks the takeover table:
//
//   no payment row            → normal flow with X
//   Pending                   → normal flow with X
//   Processing + NotFound     → charge with X
//   Processing + Charged      → mark Succeeded, NO charge
//   Processing + Declined     → mark Failed, NO charge
//   Succeeded / Failed        → no provider call at all, complete key from the row
//
// Concurrent takers are step 3 and are not tested here.
// Needs: docker compose up -d
public class TakeoverTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";
    private const decimal Amount = 40.00m;
    private const string Currency = "USD";
    private static readonly string Json = """{"amount": 40.00, "currency": "USD"}""";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private readonly CountingProvider _provider = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private sealed class CountingProvider : IPaymentProvider
    {
        private int _charges, _statusChecks;
        public FakePaymentProvider Inner { get; } =
            new() { DefaultBehaviour = FakePaymentProvider.Behaviour.Success };
        public int Charges => Volatile.Read(ref _charges);
        public int StatusChecks => Volatile.Read(ref _statusChecks);
        public List<string> ChargedReferences { get; } = new();

        public Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            Interlocked.Increment(ref _charges);
            lock (ChargedReferences) ChargedReferences.Add(reference);
            return Inner.ChargeAsync(reference, amount, currency, ct);
        }

        public Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct)
        {
            Interlocked.Increment(ref _statusChecks);
            return Inner.GetStatusAsync(reference, ct);
        }
    }

    public async Task InitializeAsync()
    {
        await using (var db = NewContext())
            await db.Database.MigrateAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:paymentdb", ConnectionString);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IPaymentProvider>();
                s.AddSingleton<IPaymentProvider>(_provider);
                s.RemoveAll<IdempotencyOptions>();
                s.AddSingleton(new IdempotencyOptions(StaleAfter));
            });
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    // ── planting "A died here" ─────────────────────────────────────────────

    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    // A claimed the key (with reference X) and never completed it.
    private static async Task<(string Key, Guid X)> PlantClaim(DateTime createdAt, Guid? paymentId = null, string? hash = null)
    {
        var key = "m4t-" + Guid.NewGuid();
        var x = paymentId ?? Guid.NewGuid();
        await using var db = NewContext();
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Key = key,
            RequestHash = hash ?? RequestFingerprint.Compute(Amount, Currency),
            PaymentId = paymentId == Guid.Empty ? null : x,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return (key, x);
    }

    private static async Task PlantPayment(Guid x, PaymentStatus status, string? providerReference = null)
    {
        await using var db = NewContext();
        db.Payments.Add(new Payment
        {
            Id = x,
            Amount = Amount,
            Currency = Currency,
            Status = status,
            ProviderReference = providerReference,
            CreatedAt = LongAgo,
            UpdatedAt = LongAgo,
            CompletedAt = status is PaymentStatus.Succeeded or PaymentStatus.Failed ? LongAgo : null,
        });
        await db.SaveChangesAsync();
    }

    // Makes the provider "remember" X as charged/declined, as if A's call reached it.
    private async Task<ProviderResult> ProviderAlreadySaw(Guid x, FakePaymentProvider.Behaviour behaviour)
    {
        _provider.Inner.Configure(x.ToString(), behaviour);
        return await _provider.Inner.ChargeAsync(x.ToString(), Amount, Currency, CancellationToken.None);
    }

    private Task<HttpResponseMessage> Retry(string key)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent(Json, System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return _client.SendAsync(msg);
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage res) =>
        await res.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<(Payment Payment, IdempotencyKey Claim)> Rows(string key, Guid x)
    {
        await using var db = NewContext();
        return (await db.Payments.SingleAsync(p => p.Id == x), await db.IdempotencyKeys.SingleAsync(k => k.Key == key));
    }

    // ── not stale: still 409 ───────────────────────────────────────────────

    [Fact]
    public async Task Recent_claim_without_response_is_409_and_touches_nothing()
    {
        var (key, x) = await PlantClaim(DateTime.UtcNow);
        await PlantPayment(x, PaymentStatus.Processing);

        var res = await Retry(key);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _provider.Charges.Should().Be(0);
        _provider.StatusChecks.Should().Be(0);
    }

    [Fact]
    public async Task Stale_claim_with_a_different_body_is_still_422_not_a_takeover()
    {
        var (key, x) = await PlantClaim(LongAgo, hash: RequestFingerprint.Compute(99.00m, "USD"));
        await PlantPayment(x, PaymentStatus.Pending);

        var res = await Retry(key);

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _provider.Charges.Should().Be(0);
    }

    // ── takeover table ─────────────────────────────────────────────────────

    [Fact]
    public async Task Stale_claim_no_payment_row_charges_once_with_X()
    {
        var (key, x) = await PlantClaim(LongAgo);

        var res = await Retry(key);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BodyOf(res)).GetProperty("id").GetGuid().Should().Be(x);
        _provider.ChargedReferences.Should().Equal(x.ToString());
        var (payment, claim) = await Rows(key, x);
        payment.Status.Should().Be(PaymentStatus.Succeeded);
        claim.ResponseStatusCode.Should().Be(201);
    }

    [Fact]
    public async Task Stale_claim_Pending_charges_once_with_X()
    {
        var (key, x) = await PlantClaim(LongAgo);
        await PlantPayment(x, PaymentStatus.Pending);

        var res = await Retry(key);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        _provider.ChargedReferences.Should().Equal(x.ToString());
        var (payment, claim) = await Rows(key, x);
        payment.Status.Should().Be(PaymentStatus.Succeeded);
        payment.ProviderReference.Should().NotBeNullOrEmpty();
        claim.ResponseStatusCode.Should().Be(201);
    }

    [Fact]
    public async Task Stale_claim_Processing_provider_NotFound_charges_once_with_X()
    {
        var (key, x) = await PlantClaim(LongAgo);
        await PlantPayment(x, PaymentStatus.Processing);

        var res = await Retry(key);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        _provider.StatusChecks.Should().Be(1, "Processing must ask the provider first");
        _provider.ChargedReferences.Should().Equal(x.ToString());
        (await Rows(key, x)).Payment.Status.Should().Be(PaymentStatus.Succeeded);
    }

    [Fact]
    public async Task Stale_claim_Processing_provider_Charged_marks_Succeeded_without_charging()
    {
        var (key, x) = await PlantClaim(LongAgo);
        await PlantPayment(x, PaymentStatus.Processing);
        var atProvider = await ProviderAlreadySaw(x, FakePaymentProvider.Behaviour.Success);

        var res = await Retry(key);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        _provider.Charges.Should().Be(0, "the provider already has the money");
        _provider.StatusChecks.Should().Be(1);
        var (payment, claim) = await Rows(key, x);
        payment.Status.Should().Be(PaymentStatus.Succeeded);
        payment.ProviderReference.Should().Be(atProvider.Reference);
        payment.CompletedAt.Should().NotBeNull();
        claim.ResponseStatusCode.Should().Be(201);
        (await BodyOf(res)).GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task Stale_claim_Processing_provider_Declined_marks_Failed_without_charging()
    {
        var (key, x) = await PlantClaim(LongAgo);
        await PlantPayment(x, PaymentStatus.Processing);
        await ProviderAlreadySaw(x, FakePaymentProvider.Behaviour.Decline);

        var res = await Retry(key);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        _provider.Charges.Should().Be(0);
        var (payment, claim) = await Rows(key, x);
        payment.Status.Should().Be(PaymentStatus.Failed);
        claim.ResponseStatusCode.Should().Be(201);
        (await BodyOf(res)).GetProperty("status").GetString().Should().Be("Failed");
    }

    [Theory]
    [InlineData(PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.Failed)]
    public async Task Stale_claim_already_settled_payment_completes_key_without_any_provider_call(PaymentStatus status)
    {
        var (key, x) = await PlantClaim(LongAgo);
        await PlantPayment(x, status, status == PaymentStatus.Succeeded ? "prov-ref-123" : null);

        var res = await Retry(key);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        _provider.Charges.Should().Be(0);
        _provider.StatusChecks.Should().Be(0, "a settled payment needs nothing from the provider");
        var body = await BodyOf(res);
        body.GetProperty("id").GetGuid().Should().Be(x);
        body.GetProperty("status").GetString().Should().Be(status.ToString());
        var (payment, claim) = await Rows(key, x);
        payment.Status.Should().Be(status);
        claim.ResponseStatusCode.Should().Be(201);
        claim.ResponseBody.Should().Be(await res.Content.ReadAsStringAsync());
    }

    // ── after a takeover the key behaves like any completed key ────────────

    [Fact]
    public async Task After_takeover_the_next_retry_is_a_plain_replay()
    {
        var (key, x) = await PlantClaim(LongAgo);
        await PlantPayment(x, PaymentStatus.Processing);
        await ProviderAlreadySaw(x, FakePaymentProvider.Behaviour.Success);

        var first = await Retry(key);
        var second = await Retry(key);

        second.StatusCode.Should().Be(HttpStatusCode.Created);
        (await second.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        second.Headers.Location.Should().Be(first.Headers.Location);
        _provider.Charges.Should().Be(0);
        _provider.StatusChecks.Should().Be(1, "the second retry must replay, not take over again");
    }

    [Fact]
    public async Task Takeover_response_has_Location_of_X()
    {
        var (key, x) = await PlantClaim(LongAgo);

        var res = await Retry(key);

        res.Headers.Location!.ToString().Should().Be($"/api/payments/{x}");
    }

    // ── end to end: the real dead state from WriteAheadClaimTests ─────────

    [Fact]
    public async Task Timeout_after_charge_then_stale_retry_recovers_without_a_second_charge()
    {
        var key = "m4t-" + Guid.NewGuid();
        _provider.Inner.DefaultBehaviour = FakePaymentProvider.Behaviour.TimeoutAfterCharge;

        var dead = await Retry(key);
        ((int)dead.StatusCode).Should().BeGreaterThanOrEqualTo(500);
        _provider.Charges.Should().Be(1);

        // Age the claim instead of waiting 30 s.
        await using (var db = NewContext())
            await db.IdempotencyKeys.Where(k => k.Key == key)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.CreatedAt, LongAgo));

        var res = await Retry(key);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BodyOf(res)).GetProperty("status").GetString().Should().Be("Succeeded");
        _provider.Charges.Should().Be(1, "the customer was charged exactly once");
    }
}
