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
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace IdempotentPayments.Tests.Integration;

// M5: an Idempotency-Key is remembered for KeyLifetime (24 h) after CreatedAt.
// After that it is forgotten, and the same key starts a brand-new payment.
//
// Agreed design (2026-10-24):
//   - expiry is measured from CreatedAt (ClaimedAt is for stale takeovers)
//   - the expiry check runs BEFORE the fingerprint check (else expired keys get 422)
//   - reclaim = conditional UPDATE WHERE CreatedAt < cutoff; SET resets every
//     column of A's (RequestHash, PaymentId, ResponseStatusCode, ResponseBody,
//     CreatedAt, ClaimedAt); the winner takes the new-payment path
//   - the sweep deletes key rows past KeyLifetime (housekeeping only)
//
// Deliberately NOT pinned (still open): the exact status a reclaim loser gets,
// and what happens to an expired key whose payment never finished.
//
// The clock is a FakeTimeProvider; requests go through the real endpoint.
// Needs: docker compose up -d
[Collection(nameof(ConcurrencyCollection))]
public class KeyExpiryTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan JustBefore = Lifetime - TimeSpan.FromSeconds(1);
    private static readonly TimeSpan JustAfter = Lifetime + TimeSpan.FromSeconds(1);

    private const decimal AmountA = 25.00m;
    private const decimal AmountOther = 26.00m;

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly RecordingProvider _provider = new();
    private readonly PauseAfterKeyRead _pause = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private sealed class RecordingProvider : IPaymentProvider
    {
        private int _charges;
        public FakePaymentProvider Inner { get; } =
            new() { DefaultBehaviour = FakePaymentProvider.Behaviour.Success };
        public int Charges => Volatile.Read(ref _charges);
        public List<string> ChargedReferences { get; } = new();
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        // Runs inside ChargeAsync, before the charge "reaches" the provider.
        public Func<string, Task>? OnCharge { get; set; }

        public async Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            Interlocked.Increment(ref _charges);
            lock (ChargedReferences) ChargedReferences.Add(reference);
            if (OnCharge is not null) await OnCharge(reference);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return await Inner.ChargeAsync(reference, amount, currency, ct);
        }

        public Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct) =>
            Inner.GetStatusAsync(reference, ct);
    }

    // Once armed: right after the FIRST query that reads key K returns, run
    // Gap (another whole request) before letting the reader continue.
    private sealed class PauseAfterKeyRead : DbCommandInterceptor
    {
        public string? Key { get; set; }
        public Func<Task>? Gap { get; set; }
        public bool Fired { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && Key is { } k && Gap is not null
                && command.CommandText.Contains("FROM \"IdempotencyKeys\"")
                && command.Parameters.Cast<DbParameter>().Any(p => p.Value is string v && v == k))
            {
                Fired = true;
                await Gap();
            }
            return result;
        }
    }

    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureMigratedAsync();
        await SettleStaleRowsFromOtherTests();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:paymentdb", ConnectionString);
            b.ConfigureTestServices(s =>
            {
                TestDatabase.RemoveSweepLoop(s);
                s.RemoveAll<IPaymentProvider>();
                s.AddSingleton<IPaymentProvider>(_provider);
                s.RemoveAll<IdempotencyOptions>();
                s.AddSingleton(new IdempotencyOptions(
                    staleAfter: TimeSpan.FromSeconds(15),
                    providerTimeout: TimeSpan.FromSeconds(1),
                    keyLifetime: Lifetime));
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(_clock);
                s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(_pause));
            });
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // Once the clock jumps 24 h, every Pending/Processing row other classes left
    // behind looks stale to a sweep pass. Settle them so a pass only sees ours.
    private static async Task SettleStaleRowsFromOtherTests()
    {
        await using var db = NewContext();
        await db.Payments
            .Where(p => p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Processing)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Failed)
                .SetProperty(p => p.FailureReason, "test cleanup"));
    }

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    private static string NewKey() => "m5-" + Guid.NewGuid();

    private Task<HttpResponseMessage> Post(string key, decimal amount)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new { amount, currency = "USD" }),
        };
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return _client.SendAsync(msg);
    }

    private static async Task<Guid> IdOf(HttpResponseMessage res)
    {
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    // Request A uses the key at "now" and completes.
    private async Task<Guid> CompletedPaymentA(string key)
    {
        var res = await Post(key, AmountA);
        res.StatusCode.Should().Be(HttpStatusCode.Created, "A is a normal first request");
        return await IdOf(res);
    }

    private static async Task<IdempotencyKey?> KeyRow(string key)
    {
        await using var db = NewContext();
        return await db.IdempotencyKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Key == key);
    }

    // DeleteExpiredAsync removes at most one batch (100) per pass, oldest first.
    // Earlier runs leave thousands of finished keys older than ours, so sweep
    // tests start from an empty key table to be about OUR rows only.
    private static async Task DeleteAllKeys()
    {
        await using var db = NewContext();
        await db.IdempotencyKeys.ExecuteDeleteAsync();
    }

    private async Task SweepOnce()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PaymentSweep>().RunOnceAsync(CancellationToken.None);
    }

    // ── still inside the lifetime: M2 behaviour must not change ────────────

    [Fact]
    public async Task Just_before_expiry_same_body_replays_A_without_a_charge()
    {
        var key = NewKey();
        var a = await CompletedPaymentA(key);

        _clock.Advance(JustBefore);
        var res = await Post(key, AmountA);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        (await IdOf(res)).Should().Be(a, "the key is still remembered");
        _provider.Charges.Should().Be(1);
    }

    [Fact]
    public async Task Just_before_expiry_different_body_is_still_422()
    {
        var key = NewKey();
        await CompletedPaymentA(key);

        _clock.Advance(JustBefore);
        var res = await Post(key, AmountOther);

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _provider.Charges.Should().Be(1);
    }

    // ── past the lifetime: the key is forgotten ────────────────────────────

    [Fact]
    public async Task After_expiry_same_body_is_a_new_charge_under_a_new_id()
    {
        var key = NewKey();
        var a = await CompletedPaymentA(key);

        _clock.Advance(JustAfter);
        var res = await Post(key, AmountA);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var b = await IdOf(res);
        b.Should().NotBe(a, "an expired key must not replay A's 24-hour-old response");
        _provider.Charges.Should().Be(2);
        _provider.ChargedReferences.Last().Should().Be(b.ToString(),
            "B is charged under its own new payment id, not A's");
    }

    [Fact]
    public async Task After_expiry_different_body_is_a_new_charge_not_422()
    {
        var key = NewKey();
        var a = await CompletedPaymentA(key);

        _clock.Advance(JustAfter);
        var res = await Post(key, AmountOther);

        res.StatusCode.Should().Be(HttpStatusCode.Created,
            "the expiry check must run before the fingerprint check");
        var b = await IdOf(res);
        b.Should().NotBe(a);
        _provider.Charges.Should().Be(2);

        await using var db = NewContext();
        (await db.Payments.SingleAsync(p => p.Id == b)).Amount.Should().Be(AmountOther);
    }

    [Fact]
    public async Task After_expiry_A_payment_row_is_left_untouched()
    {
        var key = NewKey();
        var a = await CompletedPaymentA(key);

        _clock.Advance(JustAfter);
        await Post(key, AmountOther);

        await using var db = NewContext();
        var rowA = await db.Payments.SingleAsync(p => p.Id == a);
        rowA.Status.Should().Be(PaymentStatus.Succeeded);
        rowA.Amount.Should().Be(AmountA);
    }

    [Fact]
    public async Task After_reclaim_the_key_row_belongs_entirely_to_B()
    {
        var key = NewKey();
        var a = await CompletedPaymentA(key);

        _clock.Advance(JustAfter);
        var res = await Post(key, AmountOther);
        var b = await IdOf(res);

        var row = await KeyRow(key);
        row.Should().NotBeNull();
        row!.PaymentId.Should().Be(b);
        row.RequestHash.Should().Be(RequestFingerprint.Compute(AmountOther, "USD"));
        row.ResponseStatusCode.Should().Be(201);
        row.ResponseBody.Should().Contain(b.ToString()).And.NotContain(a.ToString());
        row.CreatedAt.Should().BeCloseTo(_clock.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(1),
            "CreatedAt must restart, or the key would stay expired forever");
        row.ClaimedAt.Should().BeCloseTo(_clock.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(1));
    }

    // Write-ahead check: while B's charge is in flight, nothing of A's response
    // may survive in the row. Catches a SET that forgets ResponseStatusCode/Body.
    [Fact]
    public async Task While_B_is_being_charged_no_part_of_A_response_remains_in_the_row()
    {
        var key = NewKey();
        await CompletedPaymentA(key);

        IdempotencyKey? seen = null;
        _provider.OnCharge = async _ => seen = await KeyRow(key);

        _clock.Advance(JustAfter);
        var res = await Post(key, AmountOther);
        var b = await IdOf(res);

        seen.Should().NotBeNull();
        seen!.PaymentId.Should().Be(b, "the new id is written before the charge (write-ahead)");
        seen.ResponseBody.Should().BeNull("A's response must be cleared by the reclaim");
        seen.ResponseStatusCode.Should().BeNull("A's status code must be cleared by the reclaim too");
        seen.RequestHash.Should().Be(RequestFingerprint.Compute(AmountOther, "USD"));
    }

    [Fact]
    public async Task A_reclaimed_key_lives_a_fresh_24_hours_from_the_reclaim()
    {
        var key = NewKey();
        await CompletedPaymentA(key);

        _clock.Advance(JustAfter);
        var b = await IdOf(await Post(key, AmountA));

        _clock.Advance(JustBefore);
        var replay = await Post(key, AmountA);
        (await IdOf(replay)).Should().Be(b, "B's key is only 24h - 1s old");
        _provider.Charges.Should().Be(2);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var c = await IdOf(await Post(key, AmountA));
        c.Should().NotBe(b, "now B's key has expired too");
        _provider.Charges.Should().Be(3);
    }

    // ── concurrency: many retries of one expired key ───────────────────────

    [Fact]
    public async Task Twenty_concurrent_retries_of_an_expired_key_charge_exactly_once_more()
    {
        var key = NewKey();
        var a = await CompletedPaymentA(key);

        _provider.Delay = TimeSpan.FromMilliseconds(200); // every retry overlaps the charge
        _clock.Advance(JustAfter);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Post(key, AmountA)));

        _provider.Charges.Should().Be(2, "A once, then exactly one new charge for all 20 retries");
        responses.Select(r => r.StatusCode).Should().NotContain(HttpStatusCode.InternalServerError);

        var winners = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        winners.Should().NotBeEmpty();
        var ids = await Task.WhenAll(winners.Select(IdOf));
        ids.Should().OnlyContain(id => id != a, "no retry may be handed A's expired response");
        ids.Distinct().Should().ContainSingle("every 201 must describe the one new payment");

        var newId = ids[0];
        (await KeyRow(key))!.PaymentId.Should().Be(newId);
    }

    [Fact]
    public async Task Concurrent_retries_with_mixed_bodies_after_expiry_still_charge_once()
    {
        var key = NewKey();
        var a = await CompletedPaymentA(key);

        _provider.Delay = TimeSpan.FromMilliseconds(200);
        _clock.Advance(JustAfter);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Post(key, i % 2 == 0 ? AmountA : AmountOther)));

        _provider.Charges.Should().Be(2, "one reclaim winner, whatever body it carried");
        responses.Select(r => r.StatusCode).Should().NotContain(HttpStatusCode.InternalServerError);

        var winners = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        var ids = await Task.WhenAll(winners.Select(IdOf));
        ids.Should().OnlyContain(id => id != a);
        ids.Distinct().Count().Should().BeLessThanOrEqualTo(1);
    }

    // ABA: B reads A's expired row. Before B's reclaim UPDATE runs, C reclaims,
    // charges and COMPLETES the key. The row now again has a ResponseBody, so
    // "ResponseBody != null" alone would let B reclaim a second time. Only the
    // CreatedAt == seen check tells B the row is no longer the one it read.
    //
    //   t0  B: SELECT K               → A's row (expired, finished)
    //   t1  C: reclaim, charge, complete  → K is C's, finished again
    //   t2  B: UPDATE … WHERE CreatedAt == A's CreatedAt → must match 0 rows
    [Theory]
    [InlineData(false)] // B has the same body as C → replay C
    [InlineData(true)]  // B has a different body  → 422 against C's fingerprint
    public async Task Reclaim_that_lost_to_a_completed_reclaim_does_not_charge_again(bool bDiffers)
    {
        var key = NewKey();
        var a = await CompletedPaymentA(key);
        _clock.Advance(JustAfter);

        Guid? c = null;
        _pause.Key = key;
        _pause.Gap = async () =>
        {
            var resC = await Post(key, AmountA);
            resC.StatusCode.Should().Be(HttpStatusCode.Created, "C runs alone and wins the reclaim");
            c = await IdOf(resC);
        };

        var resB = await Post(key, bDiffers ? AmountOther : AmountA);
        var why = $"B got {(int)resB.StatusCode}: {await resB.Content.ReadAsStringAsync()}";

        _pause.Fired.Should().BeTrue("the test must actually hit the window");
        c.Should().NotBeNull().And.NotBe(a);
        _provider.Charges.Should().Be(2, "A once and C once; B arrived for the same expired row and must not charge. " + why);
        (await KeyRow(key))!.PaymentId.Should().Be(c, "C's reclaim must not be overwritten by B");

        if (bDiffers)
            resB.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, why);
        else
        {
            resB.StatusCode.Should().Be(HttpStatusCode.Created, why);
            (await IdOf(resB)).Should().Be(c!.Value, "B is a retry of C's still-fresh key");
        }
    }

    // ── the sweep: housekeeping deletes expired key rows ───────────────────

    [Fact]
    public async Task Sweep_deletes_key_rows_past_the_lifetime_and_keeps_younger_ones()
    {
        await DeleteAllKeys();
        var oldKey = NewKey();
        var a = await CompletedPaymentA(oldKey);

        _clock.Advance(TimeSpan.FromHours(12));
        var youngKey = NewKey();
        await CompletedPaymentA(youngKey);

        _clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1)); // old: 24h+1s, young: 12h+1s
        await SweepOnce();

        (await KeyRow(oldKey)).Should().BeNull("it is past KeyLifetime");
        (await KeyRow(youngKey)).Should().NotBeNull("it is only 12 hours old");

        await using var db = NewContext();
        (await db.Payments.AnyAsync(p => p.Id == a)).Should().BeTrue(
            "the sweep forgets keys, never payments");
    }

    [Fact]
    public async Task Sweep_keeps_a_key_just_before_its_lifetime()
    {
        await DeleteAllKeys();
        var key = NewKey();
        await CompletedPaymentA(key);

        _clock.Advance(JustBefore);
        await SweepOnce();

        (await KeyRow(key)).Should().NotBeNull();
    }

    [Fact]
    public async Task After_the_sweep_deleted_a_key_reusing_it_is_a_normal_new_payment()
    {
        await DeleteAllKeys();
        var key = NewKey();
        var a = await CompletedPaymentA(key);

        _clock.Advance(JustAfter);
        await SweepOnce();
        var res = await Post(key, AmountOther);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        (await IdOf(res)).Should().NotBe(a);
        _provider.Charges.Should().Be(2);
    }

    // ── options ────────────────────────────────────────────────────────────

    [Fact]
    public void KeyLifetime_defaults_to_24_hours()
    {
        new IdempotencyOptions(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1))
            .KeyLifetime.Should().Be(TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData(15)] // equal to StaleAfter
    [InlineData(10)] // shorter than StaleAfter
    public void KeyLifetime_not_longer_than_StaleAfter_is_refused(int lifetimeSeconds)
    {
        var act = () => new IdempotencyOptions(
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(lifetimeSeconds));
        act.Should().Throw<InvalidOperationException>();
    }
}
