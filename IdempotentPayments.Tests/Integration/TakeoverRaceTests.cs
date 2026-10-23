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

// M4 step 3: many retries find the SAME stale claim at the SAME time.
// Exactly one may take over. Charges are counted per CALL, so provider-side
// dedup cannot hide a second charge.
// Needs: docker compose up -d
[Collection(nameof(ConcurrencyCollection))]
public class TakeoverRaceTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";
    private const int N = 20;
    private const string Json = """{"amount": 45.00, "currency": "USD"}""";

    private readonly SlowCountingProvider _provider = new(TimeSpan.FromMilliseconds(200));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private sealed class SlowCountingProvider(TimeSpan delay) : IPaymentProvider
    {
        private int _charges, _statusChecks;
        public FakePaymentProvider Inner { get; } =
            new() { DefaultBehaviour = FakePaymentProvider.Behaviour.Success };
        public int Charges => Volatile.Read(ref _charges);
        public int StatusChecks => Volatile.Read(ref _statusChecks);

        public async Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            Interlocked.Increment(ref _charges);
            await Task.Delay(delay, CancellationToken.None);
            return await Inner.ChargeAsync(reference, amount, currency, ct);
        }

        public async Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct)
        {
            Interlocked.Increment(ref _statusChecks);
            await Task.Delay(delay, CancellationToken.None);
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
                s.RemoveAll<IPaymentProvider>();
                s.AddSingleton<IPaymentProvider>(_provider);
                s.RemoveAll<IdempotencyOptions>();
                s.AddSingleton(TestDatabase.Options);
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

    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private static async Task<(string Key, Guid X)> PlantStaleClaim(PaymentStatus? paymentStatus)
    {
        var key = "m4r-" + Guid.NewGuid();
        var x = Guid.NewGuid();
        await using var db = NewContext();
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Key = key,
            RequestHash = RequestFingerprint.Compute(45.00m, "USD"),
            PaymentId = x,
            CreatedAt = LongAgo,
            ClaimedAt = LongAgo,
        });
        if (paymentStatus is not null)
            db.Payments.Add(new Payment
            {
                Id = x, Amount = 45.00m, Currency = "USD", Status = paymentStatus.Value,
                CreatedAt = LongAgo, UpdatedAt = LongAgo,
            });
        await db.SaveChangesAsync();
        return (key, x);
    }

    private async Task<HttpStatusCode[]> FireConcurrently(string key)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, N).Select(async _ =>
        {
            await gate.Task;
            var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
            {
                Content = new StringContent(Json, System.Text.Encoding.UTF8, "application/json"),
            };
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            return (await _client.SendAsync(msg)).StatusCode;
        }).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static string Describe(HttpStatusCode[] s) =>
        string.Join(", ", s.GroupBy(x => (int)x).Select(g => $"{g.Count()}×{g.Key}"));

    // Processing + NotFound is the worst row: every taker would GetStatus AND charge.
    [Fact]
    public async Task Concurrent_takers_on_Processing_NotFound_ask_once_and_charge_once()
    {
        var (key, _) = await PlantStaleClaim(PaymentStatus.Processing);

        var statuses = await FireConcurrently(key);
        var why = $"responses were: {Describe(statuses)}";

        _provider.StatusChecks.Should().Be(1, why);
        _provider.Charges.Should().Be(1, why);
        statuses.Should().OnlyContain(s => s == HttpStatusCode.Created || s == HttpStatusCode.Conflict, why);
        statuses.Count(s => s == HttpStatusCode.Created).Should().BeGreaterThanOrEqualTo(1, why);
    }

    [Fact]
    public async Task Concurrent_takers_with_no_payment_row_charge_once()
    {
        var (key, x) = await PlantStaleClaim(null);

        var statuses = await FireConcurrently(key);

        _provider.Charges.Should().Be(1, $"responses were: {Describe(statuses)}");
        await using var db = NewContext();
        (await db.Payments.CountAsync(p => p.Id == x)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_takers_on_Pending_charge_once()
    {
        var (key, _) = await PlantStaleClaim(PaymentStatus.Pending);

        var statuses = await FireConcurrently(key);

        _provider.Charges.Should().Be(1, $"responses were: {Describe(statuses)}");
        statuses.Should().NotContain(s => (int)s >= 500);
    }

    [Fact]
    public async Task Winner_refreshes_ClaimedAt_but_not_CreatedAt()
    {
        var (key, _) = await PlantStaleClaim(PaymentStatus.Processing);

        await FireConcurrently(key);

        await using var db = NewContext();
        var claim = await db.IdempotencyKeys.SingleAsync(k => k.Key == key);
        claim.ClaimedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        claim.CreatedAt.Should().BeCloseTo(LongAgo, TimeSpan.FromSeconds(1), "CreatedAt is for M5 expiry");
        claim.ResponseStatusCode.Should().Be(201);
    }

    [Fact]
    public async Task After_the_race_every_retry_replays_the_winners_response()
    {
        var (key, _) = await PlantStaleClaim(PaymentStatus.Processing);
        await FireConcurrently(key);
        var chargesAfterRace = _provider.Charges;

        var statuses = await FireConcurrently(key);

        statuses.Should().OnlyContain(s => s == HttpStatusCode.Created, Describe(statuses));
        _provider.Charges.Should().Be(chargesAfterRace);
    }
}
