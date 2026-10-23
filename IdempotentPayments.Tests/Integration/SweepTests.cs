using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using IdempotentPayments.Api.Services;
using IdempotentPayments.Infrastructure.Data;
using IdempotentPayments.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IdempotentPayments.Tests.Integration;

// M4 step D: the sweep settles payments stuck in Pending/Processing even when
// no client ever retries. Tests call RunOnceAsync directly (one pass).
// The hosted loop is removed so it can't interfere.
// Runs alone: a sweep pass touches EVERY stale row in payment_test, so other
// classes' planted rows must not be swept mid-test (and vice versa).
// Needs: docker compose up -d
[Collection(nameof(ConcurrencyCollection))]
public class SweepTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private readonly ScriptedProvider _provider = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private sealed class ScriptedProvider : IPaymentProvider
    {
        private int _charges, _statusChecks;
        public FakePaymentProvider Inner { get; } =
            new() { DefaultBehaviour = FakePaymentProvider.Behaviour.Success };
        public int Charges => Volatile.Read(ref _charges);
        public int StatusChecks => Volatile.Read(ref _statusChecks);
        public bool HangStatus { get; set; }
        public HashSet<string> ThrowStatusFor { get; } = new();

        // When set, ChargeAsync signals ChargeEntered and waits for ReleaseCharge.
        public TaskCompletionSource? ChargeEntered { get; set; }
        public TaskCompletionSource? ReleaseCharge { get; set; }

        public async Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            Interlocked.Increment(ref _charges);
            if (ReleaseCharge is not null)
            {
                ChargeEntered?.TrySetResult();
                await ReleaseCharge.Task.WaitAsync(ct);
            }
            return await Inner.ChargeAsync(reference, amount, currency, ct);
        }

        public async Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct)
        {
            Interlocked.Increment(ref _statusChecks);
            if (HangStatus) await Task.Delay(Timeout.Infinite, ct);
            if (ThrowStatusFor.Contains(reference)) throw new HttpRequestException("provider 502");
            return await Inner.GetStatusAsync(reference, ct);
        }
    }

    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureMigratedAsync();
        await ClearStaleRowsFromOtherTests();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:paymentdb", ConnectionString);
            b.ConfigureTestServices(s =>
            {
                TestDatabase.RemoveSweepLoop(s);
                s.RemoveAll<IPaymentProvider>();
                s.AddSingleton<IPaymentProvider>(_provider);
                s.RemoveAll<IdempotencyOptions>();
                s.AddSingleton(TestDatabase.Options); // StaleAfter 15 s, ProviderTimeout 1 s
            });
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // Other test classes leave stale Pending/Processing rows behind on purpose.
    // Settle them so a sweep pass here only sees what this test planted.
    private static async Task ClearStaleRowsFromOtherTests()
    {
        await using var db = NewContext();
        await db.Payments
            .Where(p => p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Processing)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Failed)
                .SetProperty(p => p.FailureReason, "test cleanup"));
    }

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    private async Task<int> SweepOnce(CancellationToken ct = default)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentSweep>().RunOnceAsync(ct);
    }

    private static async Task<Guid> PlantPayment(PaymentStatus status, DateTime updatedAt)
    {
        var x = Guid.NewGuid();
        await using var db = NewContext();
        db.Payments.Add(new Payment
        {
            Id = x, Amount = 60.00m, Currency = "USD", Status = status,
            CreatedAt = updatedAt, UpdatedAt = updatedAt,
        });
        await db.SaveChangesAsync();
        return x;
    }

    private static async Task<string> PlantEmptyClaim(Guid x, DateTime claimedAt)
    {
        var key = "m4d-" + Guid.NewGuid();
        await using var db = NewContext();
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Key = key, RequestHash = RequestFingerprint.Compute(60.00m, "USD"),
            PaymentId = x, CreatedAt = claimedAt, ClaimedAt = claimedAt,
        });
        await db.SaveChangesAsync();
        return key;
    }

    private Task<ProviderResult> ProviderAlreadySaw(Guid x, FakePaymentProvider.Behaviour b)
    {
        _provider.Inner.Configure(x.ToString(), b);
        return _provider.Inner.ChargeAsync(x.ToString(), 60.00m, "USD", CancellationToken.None);
    }

    private static async Task<Payment> Load(Guid x)
    {
        await using var db = NewContext();
        return await db.Payments.SingleAsync(p => p.Id == x);
    }

    private static async Task<IdempotencyKey> LoadKey(string key)
    {
        await using var db = NewContext();
        return await db.IdempotencyKeys.SingleAsync(k => k.Key == key);
    }

    private Task<HttpResponseMessage> Post(string key)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent("""{"amount": 60.00, "currency": "USD"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return _client.SendAsync(msg);
    }

    // ── the sweep table ────────────────────────────────────────────────────

    [Fact]
    public async Task Stale_Pending_is_Failed_never_sent_without_asking_the_provider()
    {
        var x = await PlantPayment(PaymentStatus.Pending, LongAgo);

        await SweepOnce();

        var p = await Load(x);
        p.Status.Should().Be(PaymentStatus.Failed);
        p.FailureReason.Should().Be(SweepMarkers.NeverSent);
        p.CompletedAt.Should().NotBeNull();
        _provider.StatusChecks.Should().Be(0);
        _provider.Charges.Should().Be(0);
    }

    [Fact]
    public async Task Stale_Processing_Charged_becomes_Succeeded_with_reference()
    {
        var x = await PlantPayment(PaymentStatus.Processing, LongAgo);
        var atProvider = await ProviderAlreadySaw(x, FakePaymentProvider.Behaviour.Success);

        await SweepOnce();

        var p = await Load(x);
        p.Status.Should().Be(PaymentStatus.Succeeded);
        p.ProviderReference.Should().Be(atProvider.Reference);
        p.FailureReason.Should().BeNull();
        _provider.Charges.Should().Be(0, "the sweep never charges");
    }

    [Fact]
    public async Task Stale_Processing_Declined_becomes_Failed()
    {
        var x = await PlantPayment(PaymentStatus.Processing, LongAgo);
        await ProviderAlreadySaw(x, FakePaymentProvider.Behaviour.Decline);

        await SweepOnce();

        (await Load(x)).Status.Should().Be(PaymentStatus.Failed);
    }

    [Fact]
    public async Task Stale_Processing_NotFound_is_Failed_with_the_reconciliation_marker_and_not_charged()
    {
        var x = await PlantPayment(PaymentStatus.Processing, LongAgo);

        await SweepOnce();

        var p = await Load(x);
        p.Status.Should().Be(PaymentStatus.Failed);
        p.FailureReason.Should().Be(PaymentOutcomeMapper.FailureReasonFor(ProviderOutcome.NotFound));
        _provider.Charges.Should().Be(0);
    }

    [Fact]
    public async Task Recent_Processing_and_settled_rows_are_left_alone()
    {
        var recent = await PlantPayment(PaymentStatus.Processing, DateTime.UtcNow);
        var done = await PlantPayment(PaymentStatus.Succeeded, LongAgo);

        await SweepOnce();

        (await Load(recent)).Status.Should().Be(PaymentStatus.Processing);
        (await Load(done)).Status.Should().Be(PaymentStatus.Succeeded);
        _provider.StatusChecks.Should().Be(0);
    }

    [Fact]
    public async Task Provider_error_on_one_row_does_not_stop_the_others()
    {
        var broken = await PlantPayment(PaymentStatus.Processing, LongAgo);
        var fine = await PlantPayment(PaymentStatus.Processing, LongAgo);
        _provider.ThrowStatusFor.Add(broken.ToString());

        await SweepOnce();

        (await Load(broken)).Status.Should().Be(PaymentStatus.Processing, "left for the next pass");
        (await Load(fine)).Status.Should().Be(PaymentStatus.Failed);
    }

    // ── key rows ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_completes_the_empty_claim_so_a_retry_replays_instead_of_409()
    {
        var x = await PlantPayment(PaymentStatus.Processing, LongAgo);
        await ProviderAlreadySaw(x, FakePaymentProvider.Behaviour.Success);
        var key = await PlantEmptyClaim(x, DateTime.UtcNow); // claim NOT stale → would be 409

        await SweepOnce();

        var claim = await LoadKey(key);
        claim.ResponseStatusCode.Should().Be(201);
        var retry = await Post(key);
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        (await retry.Content.ReadAsStringAsync()).Should().Be(claim.ResponseBody);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(x);
        body.GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task Swept_body_has_the_same_shape_as_a_normal_201()
    {
        var normal = await Post("m4d-normal-" + Guid.NewGuid());
        var normalBody = await normal.Content.ReadFromJsonAsync<JsonElement>();

        var x = await PlantPayment(PaymentStatus.Processing, LongAgo);
        await ProviderAlreadySaw(x, FakePaymentProvider.Behaviour.Success);
        var key = await PlantEmptyClaim(x, DateTime.UtcNow);
        await SweepOnce();
        var swept = JsonDocument.Parse((await LoadKey(key)).ResponseBody!).RootElement;

        swept.EnumerateObject().Select(p => p.Name)
            .Should().Equal(normalBody.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Sweep_never_overwrites_a_completed_key()
    {
        var x = await PlantPayment(PaymentStatus.Processing, LongAgo);
        await ProviderAlreadySaw(x, FakePaymentProvider.Behaviour.Success);
        var key = "m4d-" + Guid.NewGuid();
        await using (var db = NewContext())
        {
            db.IdempotencyKeys.Add(new IdempotencyKey
            {
                Key = key, RequestHash = "h", PaymentId = x, CreatedAt = LongAgo, ClaimedAt = LongAgo,
                ResponseStatusCode = 201, ResponseBody = "{\"original\":true}",
            });
            await db.SaveChangesAsync();
        }

        await SweepOnce();

        (await LoadKey(key)).ResponseBody.Should().Be("{\"original\":true}");
    }

    [Fact]
    public async Task Stuck_payment_without_any_key_row_is_still_settled()
    {
        var x = await PlantPayment(PaymentStatus.Processing, LongAgo);

        var settled = await SweepOnce();

        settled.Should().Be(1);
        (await Load(x)).Status.Should().Be(PaymentStatus.Failed);
    }

    // ── concurrency ────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_sweep_passes_at_once_settle_each_payment_exactly_once()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 10; i++)
            ids.Add(await PlantPayment(PaymentStatus.Pending, LongAgo));

        var counts = await Task.WhenAll(SweepOnce(), SweepOnce());

        counts.Sum().Should().Be(10, "each row is won by exactly one pass");
        foreach (var x in ids)
            (await Load(x)).Status.Should().Be(PaymentStatus.Failed);
    }

    // ── EXPECTED TO FAIL against the current code (review findings) ───────

    // Step B's rule: EVERY provider call is bounded by ProviderTimeout.
    // The sweep calls GetStatusAsync with only the stopping token.
    [Fact]
    public async Task Hanging_status_check_does_not_hang_the_sweep()
    {
        var x = await PlantPayment(PaymentStatus.Processing, LongAgo);
        _provider.HangStatus = true;

        var sw = Stopwatch.StartNew();
        var pass = SweepOnce();
        var finished = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(pass, "ProviderTimeout is 1 s; a pass must not hang on one provider call");
        (await Load(x)).Status.Should().Be(PaymentStatus.Processing, "unknown status → leave it");
    }

    // A takeover is charging X right now (it won the claim, so it OWNS the payment).
    // The payment's UpdatedAt is still old because ResumeAsync doesn't touch it before calling
    // the provider. The sweep must not decide X's fate behind the owner's back.
    [Fact]
    public async Task Sweep_does_not_fail_a_payment_that_a_live_takeover_is_charging()
    {
        var x = await PlantPayment(PaymentStatus.Processing, LongAgo);
        var key = await PlantEmptyClaim(x, LongAgo);
        _provider.ChargeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.ReleaseCharge = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var takeover = Post(key); // stale claim → takeover → NotFound → charge (held at the gate)
        await _provider.ChargeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await SweepOnce();
        var duringCharge = await Load(x);

        _provider.ReleaseCharge.SetResult();
        var res = await takeover;

        duringCharge.Status.Should().Be(PaymentStatus.Processing,
            "the sweep marked a payment Failed while its owner was mid-charge");
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Load(x)).Status.Should().Be(PaymentStatus.Succeeded);
    }
}
