using IdempotentPayments.Infrastructure.Services;

namespace IdempotentPayments.Tests.Unit;

public class FakePaymentProviderTests
{
    [Fact]
    public async Task Unconfigured_reference_throws_InvalidOperationException()
    {
        var provider = new FakePaymentProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ChargeAsync("never-configured", 5000m, "KES", CancellationToken.None)
        );
    }

    [Fact]
    public async Task Success_charge_returns_reference_that_GetStatus_also_returns()
    {
        var provider = new FakePaymentProvider();
        provider.Configure("p1", FakePaymentProvider.Behaviour.Success);

        var charge = await provider.ChargeAsync("p1", 5000m, "KES", CancellationToken.None);
        var status = await provider.GetStatusAsync("p1", CancellationToken.None);

        Assert.Equal(ProviderOutcome.Charged, charge.Outcome);
        Assert.Equal(charge.Reference, status.Reference);
    }

    [Fact]
    public async Task Charging_same_reference_twice_sequentially_returns_same_provider_reference()
    {
        var provider = new FakePaymentProvider();
        provider.Configure("p1", FakePaymentProvider.Behaviour.Success);

        var first = await provider.ChargeAsync("p1", 5000m, "KES", CancellationToken.None);
        var second = await provider.ChargeAsync("p1", 5000m, "KES", CancellationToken.None);

        Assert.Equal(first.Reference, second.Reference);
    }

    [Fact]
    public async Task Timeout_after_charge_throws_like_HttpClient_but_GetStatus_finds_the_charge()
    {
        var provider = new FakePaymentProvider();
        provider.Configure("p1", FakePaymentProvider.Behaviour.TimeoutAfterCharge);

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            provider.ChargeAsync("p1", 5000m, "KES", CancellationToken.None)
        );
        var status = await provider.GetStatusAsync("p1", CancellationToken.None);

        Assert.Equal(ProviderOutcome.Charged, status.Outcome);
    }

    [Fact]
    public async Task Timeout_before_charge_leaves_nothing_at_the_provider()
    {
        var provider = new FakePaymentProvider();
        provider.Configure("p1", FakePaymentProvider.Behaviour.TimeoutBeforeCharge);

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            provider.ChargeAsync("p1", 5000m, "KES", CancellationToken.None)
        );
        var status = await provider.GetStatusAsync("p1", CancellationToken.None);

        Assert.Equal(ProviderOutcome.NotFound, status.Outcome);
    }

    // Option B: the provider remembers declines, so recovery sees Declined, not NotFound.
    [Fact]
    public async Task Declined_charge_is_remembered_by_GetStatus()
    {
        var provider = new FakePaymentProvider();
        provider.Configure("p1", FakePaymentProvider.Behaviour.Decline);

        var charge = await provider.ChargeAsync("p1", 5000m, "KES", CancellationToken.None);
        var status = await provider.GetStatusAsync("p1", CancellationToken.None);

        Assert.Equal(ProviderOutcome.Declined, charge.Outcome);
        Assert.Equal(ProviderOutcome.Declined, status.Outcome);
    }

    // The provider must dedupe on reference even when many charges arrive at once:
    // every caller has to see the ONE charge the provider recorded.
    [Fact]
    public async Task Concurrent_charges_for_same_reference_all_see_one_provider_reference()
    {
        const int iterations = 200;
        const int callers = 16;
        int badIterations = 0;

        for (int i = 0; i < iterations; i++)
        {
            var provider = new FakePaymentProvider();
            provider.Configure("p1", FakePaymentProvider.Behaviour.Success);
            using var start = new Barrier(callers);

            var tasks = Enumerable
                .Range(0, callers)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        start.SignalAndWait();
                        return provider.ChargeAsync("p1", 5000m, "KES", CancellationToken.None);
                    })
                )
                .ToArray();
            var results = await Task.WhenAll(tasks);
            var stored = await provider.GetStatusAsync("p1", CancellationToken.None);

            if (results.Any(r => r.Reference != stored.Reference))
                badIterations++;
        }

        Assert.True(
            badIterations == 0,
            $"{badIterations}/{iterations} runs: some callers got a provider reference that the provider never stored"
        );
    }
}
