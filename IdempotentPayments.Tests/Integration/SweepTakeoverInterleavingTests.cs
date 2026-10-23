using System.Data.Common;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using IdempotentPayments.Infrastructure.Data;
using IdempotentPayments.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IdempotentPayments.Tests.Integration;

// M4 known gaps, forced deterministically.
//
// The race: a takeover LOADS payment X, then — before it writes anything — the sweep
// settles X. The window is microseconds wide, so an EF Core interceptor plays the
// sweep's update on a separate connection immediately after the takeover's
// SELECT of X returns. The takeover therefore holds a stale in-memory copy of X.
//
// Required outcome either way: no 5xx, no charge, the sweep's settlement is not
// overwritten, and if the key row gets an answer it matches the payment row.
// (Whether the client gets 201-with-Failed or 409 is your design choice.)
// Needs: docker compose up -d
public class SweepTakeoverInterleavingTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";
    private const string Json = """{"amount": 65.00, "currency": "USD"}""";
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private readonly CountingProvider _provider = new();
    private readonly SweepSimulator _sweepLandsHere = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private sealed class CountingProvider : IPaymentProvider
    {
        private int _charges, _statusChecks;
        public FakePaymentProvider Inner { get; } =
            new() { DefaultBehaviour = FakePaymentProvider.Behaviour.Success };
        public int Charges => Volatile.Read(ref _charges);
        public int StatusChecks => Volatile.Read(ref _statusChecks);

        public Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            Interlocked.Increment(ref _charges);
            return Inner.ChargeAsync(reference, amount, currency, ct);
        }

        public Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct)
        {
            Interlocked.Increment(ref _statusChecks);
            return Inner.GetStatusAsync(reference, ct);
        }
    }

    // After the FIRST query that reads payment X returns, settle X to Failed on a
    // separate connection — exactly what a sweep pass would write.
    private sealed class SweepSimulator : DbCommandInterceptor
    {
        public Guid? Target { get; set; }
        public string Reason { get; set; } = "";
        public bool Fired { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && Target is { } x
                && command.CommandText.Contains("FROM \"Payments\"")
                && command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid g && g == x))
            {
                Fired = true;
                await using var conn = new NpgsqlConnection(ConnectionString);
                await conn.OpenAsync(cancellationToken);
                await using var cmd = new NpgsqlCommand(
                    "UPDATE \"Payments\" SET \"Status\" = 'Failed', \"FailureReason\" = @r, " +
                    "\"UpdatedAt\" = now(), \"CompletedAt\" = now() WHERE \"Id\" = @id", conn);
                cmd.Parameters.AddWithValue("r", Reason);
                cmd.Parameters.AddWithValue("id", x);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
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
                s.AddSingleton(TestDatabase.Options);
                s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(_sweepLandsHere));
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

    private static async Task<(string Key, Guid X)> PlantStaleClaimAndPayment(PaymentStatus status)
    {
        var key = "m4g-" + Guid.NewGuid();
        var x = Guid.NewGuid();
        await using var db = NewContext();
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Key = key, RequestHash = RequestFingerprint.Compute(65.00m, "USD"),
            PaymentId = x, CreatedAt = LongAgo, ClaimedAt = LongAgo,
        });
        db.Payments.Add(new Payment
        {
            Id = x, Amount = 65.00m, Currency = "USD", Status = status,
            CreatedAt = LongAgo, UpdatedAt = LongAgo,
        });
        await db.SaveChangesAsync();
        return (key, x);
    }

    private Task<HttpResponseMessage> Post(string key)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent(Json, System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return _client.SendAsync(msg);
    }

    private async Task AssertSweepWonCleanly(HttpResponseMessage res, string key, Guid x, string reason)
    {
        _sweepLandsHere.Fired.Should().BeTrue("the test must actually hit the window");
        var why = $"response was {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}";

        ((int)res.StatusCode).Should().BeLessThan(500, why);
        _provider.Charges.Should().Be(0, "the sweep already settled X as Failed; charging now bills a Failed payment");

        await using var db = NewContext();
        var payment = await db.Payments.SingleAsync(p => p.Id == x);
        payment.Status.Should().Be(PaymentStatus.Failed, "the takeover must not overwrite the sweep's settlement");
        payment.FailureReason.Should().Be(reason);

        var claim = await db.IdempotencyKeys.SingleAsync(k => k.Key == key);
        if (claim.ResponseBody is not null)
            JsonDocument.Parse(claim.ResponseBody).RootElement.GetProperty("status").GetString()
                .Should().Be("Failed", "the stored answer must match the payment row");

        if (res.StatusCode == HttpStatusCode.Created)
            JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement
                .GetProperty("status").GetString().Should().Be("Failed", "the client must not be told anything else");
    }

    // Gap 1: Processing path. TouchOwnedAsync matches 0 rows → today: throws → 500.
    [Fact]
    public async Task Sweep_settles_Processing_payment_between_takeover_load_and_touch()
    {
        var (key, x) = await PlantStaleClaimAndPayment(PaymentStatus.Processing);
        _sweepLandsHere.Target = x;
        _sweepLandsHere.Reason = "swept: provider NotFound";

        var res = await Post(key);

        await AssertSweepWonCleanly(res, key, x, "swept: provider NotFound");
        _provider.StatusChecks.Should().Be(0, "X is already settled; there is nothing to ask the provider");
    }

    // Gap 2: Pending path. ContinueAsync saves Processing unconditionally → today:
    // overwrites the sweep's Failed and charges.
    [Fact]
    public async Task Sweep_settles_Pending_payment_between_takeover_load_and_continue()
    {
        var (key, x) = await PlantStaleClaimAndPayment(PaymentStatus.Pending);
        _sweepLandsHere.Target = x;
        _sweepLandsHere.Reason = "swept: never sent";

        var res = await Post(key);

        await AssertSweepWonCleanly(res, key, x, "swept: never sent");
    }

    // Control: without the interleaving, the same takeovers still work normally.
    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Processing)]
    public async Task Without_the_sweep_the_takeover_completes_normally(PaymentStatus status)
    {
        var (key, x) = await PlantStaleClaimAndPayment(status);

        var res = await Post(key);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        _provider.Charges.Should().Be(1);
        await using var db = NewContext();
        (await db.Payments.SingleAsync(p => p.Id == x)).Status.Should().Be(PaymentStatus.Succeeded);
    }
}
