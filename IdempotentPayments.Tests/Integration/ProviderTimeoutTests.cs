using System.Diagnostics;
using FluentAssertions;
using IdempotentPayments.Infrastructure.Data;
using IdempotentPayments.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace IdempotentPayments.Tests.Integration;

// M4 step B: every provider call is bounded by ProviderTimeout, and the options
// refuse a StaleAfter that a claimant's longest path could outlive.
// Needs: docker compose up -d
public class ProviderTimeoutTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";

    // ProviderTimeout in TestDatabase.Options is 1 s; a hang must be cut well before this.
    private static readonly TimeSpan MustReturnWithin = TimeSpan.FromSeconds(5);

    public Task InitializeAsync() => TestDatabase.EnsureMigratedAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    // Hangs until the token it is given is cancelled. Records whether it saw cancellation.
    private sealed class HangingProvider(bool hangCharge, bool hangStatus) : IPaymentProvider
    {
        public FakePaymentProvider Inner { get; } =
            new() { DefaultBehaviour = FakePaymentProvider.Behaviour.Success };
        public bool ChargeWasCancelled { get; private set; }
        public bool StatusWasCancelled { get; private set; }
        public TaskCompletionSource ChargeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            if (hangCharge)
            {
                ChargeEntered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { ChargeWasCancelled = true; throw; }
            }
            return await Inner.ChargeAsync(reference, amount, currency, ct);
        }

        public async Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct)
        {
            if (hangStatus)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { StatusWasCancelled = true; throw; }
            }
            return await Inner.GetStatusAsync(reference, ct);
        }
    }

    // ── options rule ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(70, 30)]   // equal to 2 x 30 + 10 → not strictly greater
    [InlineData(60, 30)]   // the old default
    [InlineData(11, 1)]
    public void Options_reject_StaleAfter_not_greater_than_two_timeouts_plus_margin(int stale, int timeout)
    {
        var act = () => new IdempotencyOptions(TimeSpan.FromSeconds(stale), TimeSpan.FromSeconds(timeout));

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(90, 30)]   // production defaults
    [InlineData(71, 30)]
    [InlineData(15, 1)]    // test settings
    public void Options_accept_StaleAfter_greater_than_two_timeouts_plus_margin(int stale, int timeout)
    {
        var options = new IdempotencyOptions(TimeSpan.FromSeconds(stale), TimeSpan.FromSeconds(timeout));

        options.StaleAfter.Should().Be(TimeSpan.FromSeconds(stale));
        options.ProviderTimeout.Should().Be(TimeSpan.FromSeconds(timeout));
    }

    // ── charge is bounded ──────────────────────────────────────────────────

    [Fact]
    public async Task Hanging_charge_is_cut_off_by_the_provider_timeout()
    {
        var provider = new HangingProvider(hangCharge: true, hangStatus: false);
        await using var db = NewContext();
        var service = new PaymentService(db, provider, TestDatabase.Options);
        var id = Guid.NewGuid();

        var sw = Stopwatch.StartNew();
        var act = () => service.CreateAsync(id, 50.00m, "USD", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(MustReturnWithin);
        provider.ChargeWasCancelled.Should().BeTrue();

        // Outcome unknown → the row must stay Processing for recovery, not be marked Failed.
        await using var fresh = NewContext();
        (await fresh.Payments.SingleAsync(p => p.Id == id)).Status.Should().Be(PaymentStatus.Processing);
    }

    [Fact]
    public async Task Hanging_charge_respects_the_timeout_not_just_the_callers_token()
    {
        // Caller's token never fires; only the timeout can end this.
        using var neverCancelled = new CancellationTokenSource();
        var provider = new HangingProvider(hangCharge: true, hangStatus: false);
        await using var db = NewContext();
        var service = new PaymentService(db, provider, TestDatabase.Options);

        var sw = Stopwatch.StartNew();
        var act = () => service.CreateAsync(Guid.NewGuid(), 51.00m, "USD", neverCancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(MustReturnWithin);
        neverCancelled.IsCancellationRequested.Should().BeFalse("the timeout must not cancel the caller's own source");
    }

    [Fact]
    public async Task Callers_cancellation_still_reaches_the_provider()
    {
        using var cts = new CancellationTokenSource();
        var provider = new HangingProvider(hangCharge: true, hangStatus: false);
        await using var db = NewContext();
        var service = new PaymentService(db, provider, TestDatabase.Options);

        var task = service.CreateAsync(Guid.NewGuid(), 52.00m, "USD", cts.Token);
        await provider.ChargeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // cancel only once the provider is reached
        var sw = Stopwatch.StartNew();
        cts.Cancel();

        await task.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(800), "client cancel must not wait for the 1 s timeout");
        provider.ChargeWasCancelled.Should().BeTrue();
    }

    // ── status check during takeover is bounded too ───────────────────────

    [Fact]
    public async Task Hanging_status_check_during_resume_is_cut_off_and_does_not_charge()
    {
        var x = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.Payments.Add(new Payment
            {
                Id = x, Amount = 53.00m, Currency = "USD", Status = PaymentStatus.Processing,
                CreatedAt = DateTime.UtcNow.AddHours(-1), UpdatedAt = DateTime.UtcNow.AddHours(-1),
            });
            await seed.SaveChangesAsync();
        }

        var provider = new HangingProvider(hangCharge: false, hangStatus: true);
        await using var db = NewContext();
        var service = new PaymentService(db, provider, TestDatabase.Options);

        var sw = Stopwatch.StartNew();
        var act = () => service.ResumeAsync(x, 53.00m, "USD", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(MustReturnWithin);
        provider.StatusWasCancelled.Should().BeTrue();

        // Unknown status must NOT be treated as NotFound → no charge, still Processing.
        (await provider.Inner.GetStatusAsync(x.ToString(), CancellationToken.None)).Outcome
            .Should().Be(ProviderOutcome.NotFound, "nothing was charged");
        await using var fresh = NewContext();
        (await fresh.Payments.SingleAsync(p => p.Id == x)).Status.Should().Be(PaymentStatus.Processing);
    }
}
