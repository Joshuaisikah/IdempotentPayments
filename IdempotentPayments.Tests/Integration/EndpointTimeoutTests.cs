using System.Diagnostics;
using System.Net;
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

// M4 step C: a provider timeout is "outcome unknown", not a server bug →
// 503 + Retry-After on BOTH the normal path and the takeover path; never a 500.
// Needs: docker compose up -d
public class EndpointTimeoutTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";
    private const string Json = """{"amount": 55.00, "currency": "USD"}""";
    private static readonly TimeSpan MustReturnWithin = TimeSpan.FromSeconds(5);

    private readonly SwitchableProvider _provider = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    // Can hang (until its token is cancelled) on charge and/or status; counts calls.
    private sealed class SwitchableProvider : IPaymentProvider
    {
        private int _charges;
        public FakePaymentProvider Inner { get; } =
            new() { DefaultBehaviour = FakePaymentProvider.Behaviour.Success };
        public bool HangCharge { get; set; }
        public bool HangStatus { get; set; }
        public int Charges => Volatile.Read(ref _charges);

        public async Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            Interlocked.Increment(ref _charges);
            if (HangCharge) await Task.Delay(Timeout.Infinite, ct);
            return await Inner.ChargeAsync(reference, amount, currency, ct);
        }

        public async Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct)
        {
            if (HangStatus) await Task.Delay(Timeout.Infinite, ct);
            return await Inner.GetStatusAsync(reference, ct);
        }
    }

    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureMigratedAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:paymentdb", ConnectionString);
            b.ConfigureTestServices(s =>
            {
                TestDatabase.RemoveSweepLoop(s);
                s.RemoveAll<IPaymentProvider>();
                s.AddSingleton<IPaymentProvider>(_provider);
                s.RemoveAll<IdempotencyOptions>();
                s.AddSingleton(TestDatabase.Options); // ProviderTimeout = 1 s
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

    private Task<HttpResponseMessage> Post(string key)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent(Json, System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return _client.SendAsync(msg);
    }

    private static async Task AssertIs503WithRetryAfter(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        res.Headers.RetryAfter.Should().NotBeNull();
        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain("Exception", "no stack traces to the client");
        body.Should().NotContain("   at ");
    }

    // ── normal path ────────────────────────────────────────────────────────

    [Fact]
    public async Task Hanging_charge_returns_503_with_Retry_After_quickly()
    {
        _provider.HangCharge = true;

        var sw = Stopwatch.StartNew();
        var res = await Post("m4c-" + Guid.NewGuid());

        sw.Elapsed.Should().BeLessThan(MustReturnWithin);
        await AssertIs503WithRetryAfter(res);
    }

    [Fact]
    public async Task After_a_timeout_the_claim_is_held_and_the_payment_is_Processing()
    {
        _provider.HangCharge = true;
        var key = "m4c-" + Guid.NewGuid();

        await Post(key);

        await using var db = NewContext();
        var claim = await db.IdempotencyKeys.SingleAsync(k => k.Key == key);
        claim.ResponseBody.Should().BeNull("a 503 must not be cached as the key's answer");
        claim.ResponseStatusCode.Should().BeNull();
        (await db.Payments.SingleAsync(p => p.Id == claim.PaymentId)).Status
            .Should().Be(PaymentStatus.Processing, "outcome unknown → leave it for recovery");
    }

    [Fact]
    public async Task Immediate_retry_after_a_timeout_is_409_not_a_second_charge()
    {
        _provider.HangCharge = true;
        var key = "m4c-" + Guid.NewGuid();
        await Post(key);
        var chargesAfterFirst = _provider.Charges;

        _provider.HangCharge = false;
        var retry = await Post(key);

        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _provider.Charges.Should().Be(chargesAfterFirst);
    }

    [Theory]
    [InlineData(FakePaymentProvider.Behaviour.TimeoutBeforeCharge)]
    [InlineData(FakePaymentProvider.Behaviour.TimeoutAfterCharge)]
    public async Task Fake_provider_timeouts_are_503_not_500(FakePaymentProvider.Behaviour behaviour)
    {
        _provider.Inner.DefaultBehaviour = behaviour;

        var res = await Post("m4c-" + Guid.NewGuid());

        await AssertIs503WithRetryAfter(res);
    }

    // ── takeover path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Takeover_with_hanging_status_check_returns_503_and_does_not_charge()
    {
        var key = "m4c-" + Guid.NewGuid();
        var x = Guid.NewGuid();
        var longAgo = DateTime.UtcNow.AddHours(-1);
        await using (var db = NewContext())
        {
            db.IdempotencyKeys.Add(new IdempotencyKey
            {
                Key = key, RequestHash = RequestFingerprint.Compute(55.00m, "USD"),
                PaymentId = x, CreatedAt = longAgo, ClaimedAt = longAgo,
            });
            db.Payments.Add(new Payment
            {
                Id = x, Amount = 55.00m, Currency = "USD", Status = PaymentStatus.Processing,
                CreatedAt = longAgo, UpdatedAt = longAgo,
            });
            await db.SaveChangesAsync();
        }
        _provider.HangStatus = true;

        var sw = Stopwatch.StartNew();
        var res = await Post(key);

        sw.Elapsed.Should().BeLessThan(MustReturnWithin);
        await AssertIs503WithRetryAfter(res);
        _provider.Charges.Should().Be(0, "unknown status must not be treated as NotFound");
    }

    [Fact]
    public async Task Takeover_with_hanging_charge_returns_503()
    {
        var key = "m4c-" + Guid.NewGuid();
        var x = Guid.NewGuid();
        var longAgo = DateTime.UtcNow.AddHours(-1);
        await using (var db = NewContext())
        {
            db.IdempotencyKeys.Add(new IdempotencyKey
            {
                Key = key, RequestHash = RequestFingerprint.Compute(55.00m, "USD"),
                PaymentId = x, CreatedAt = longAgo, ClaimedAt = longAgo,
            });
            await db.SaveChangesAsync(); // no payment row → takeover charges with X
        }
        _provider.HangCharge = true;

        var res = await Post(key);

        await AssertIs503WithRetryAfter(res);
    }

    // ── a timeout is not sticky: once the provider recovers, recovery works ─

    [Fact]
    public async Task Timeout_then_stale_retry_after_provider_recovers_succeeds_with_one_charge()
    {
        _provider.Inner.DefaultBehaviour = FakePaymentProvider.Behaviour.TimeoutAfterCharge;
        var key = "m4c-" + Guid.NewGuid();

        var first = await Post(key);
        await AssertIs503WithRetryAfter(first);

        _provider.Inner.DefaultBehaviour = FakePaymentProvider.Behaviour.Success;
        await using (var db = NewContext())
            await db.IdempotencyKeys.Where(k => k.Key == key)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.ClaimedAt, DateTime.UtcNow.AddHours(-1)));

        var retry = await Post(key);

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        _provider.Charges.Should().Be(1, "takeover found Charged via GetStatusAsync");
    }
}
