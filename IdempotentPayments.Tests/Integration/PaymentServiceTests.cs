using FluentAssertions;
using IdempotentPayments.Infrastructure.Data;
using IdempotentPayments.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace IdempotentPayments.Tests.Integration;

// Runs PaymentService against the real Postgres from docker-compose (port 5433),
// in a separate database "payment_test" so dev data is untouched.
// Needs: docker compose up -d
public class PaymentServiceTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Fake provider that decides the behaviour for whatever reference the service sends.
    private sealed class AlwaysProvider(FakePaymentProvider.Behaviour behaviour) : IPaymentProvider
    {
        public FakePaymentProvider Inner { get; } = new();

        public Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            Inner.Configure(reference, behaviour);
            return Inner.ChargeAsync(reference, amount, currency, ct);
        }

        public Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct) =>
            Inner.GetStatusAsync(reference, ct);
    }

    // Charges successfully, then cancels the caller's token, like a client
    // disconnecting right after the provider took the money.
    private sealed class ChargeThenCancelProvider(CancellationTokenSource cts) : IPaymentProvider
    {
        public FakePaymentProvider Inner { get; } = new();

        public async Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            Inner.Configure(reference, FakePaymentProvider.Behaviour.Success);
            var result = await Inner.ChargeAsync(reference, amount, currency, ct);
            cts.Cancel();
            return result;
        }

        public Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct) =>
            Inner.GetStatusAsync(reference, ct);
    }

    [Fact]
    public async Task Successful_charge_is_saved_as_Succeeded_with_provider_reference()
    {
        await using var db = NewContext();
        var service = new PaymentService(db, new AlwaysProvider(FakePaymentProvider.Behaviour.Success));

        var created = await service.CreateAsync(10.50m, "USD", CancellationToken.None);

        await using var fresh = NewContext();
        var row = await fresh.Payments.SingleAsync(p => p.Id == created.Id);
        row.Status.Should().Be(PaymentStatus.Succeeded);
        row.ProviderReference.Should().NotBeNullOrEmpty();
        row.Amount.Should().Be(10.50m);
        row.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task Declined_charge_is_saved_as_Failed_without_provider_reference()
    {
        await using var db = NewContext();
        var service = new PaymentService(db, new AlwaysProvider(FakePaymentProvider.Behaviour.Decline));

        var created = await service.CreateAsync(5m, "USD", CancellationToken.None);

        await using var fresh = NewContext();
        var row = await fresh.Payments.SingleAsync(p => p.Id == created.Id);
        row.Status.Should().Be(PaymentStatus.Failed);
        row.ProviderReference.Should().BeNull();
    }

    [Fact]
    public async Task Provider_receives_the_payment_id_as_reference()
    {
        await using var db = NewContext();
        var provider = new AlwaysProvider(FakePaymentProvider.Behaviour.Success);
        var service = new PaymentService(db, provider);

        var created = await service.CreateAsync(1m, "USD", CancellationToken.None);

        var status = await provider.Inner.GetStatusAsync(created.Id.ToString(), CancellationToken.None);
        status.Outcome.Should().Be(ProviderOutcome.Charged);
    }

    [Fact]
    public async Task Timestamps_are_set_and_UTC()
    {
        await using var db = NewContext();
        var service = new PaymentService(db, new AlwaysProvider(FakePaymentProvider.Behaviour.Success));
        var before = DateTime.UtcNow.AddSeconds(-5);

        var created = await service.CreateAsync(1m, "USD", CancellationToken.None);

        await using var fresh = NewContext();
        var row = await fresh.Payments.SingleAsync(p => p.Id == created.Id);
        row.CreatedAt.Should().BeAfter(before);
        row.UpdatedAt.Should().BeAfter(before, "UpdatedAt should change when the status changes");
    }

    [Fact]
    public async Task Finished_payment_has_CompletedAt()
    {
        await using var db = NewContext();
        var service = new PaymentService(db, new AlwaysProvider(FakePaymentProvider.Behaviour.Success));

        var created = await service.CreateAsync(1m, "USD", CancellationToken.None);

        await using var fresh = NewContext();
        var row = await fresh.Payments.SingleAsync(p => p.Id == created.Id);
        row.CompletedAt.Should().NotBeNull("a Succeeded or Failed payment is complete");
    }

    [Fact]
    public async Task Client_cancelling_after_the_charge_does_not_lose_the_Succeeded_status()
    {
        using var cts = new CancellationTokenSource();
        await using var db = NewContext();
        var provider = new ChargeThenCancelProvider(cts);
        var service = new PaymentService(db, provider);

        Guid? id = null;
        try
        {
            var created = await service.CreateAsync(7m, "USD", cts.Token);
            id = created.Id;
        }
        catch (OperationCanceledException) { }

        // Find the row by amount marker if the call threw before returning.
        await using var fresh = NewContext();
        var row = id is { } known
            ? await fresh.Payments.SingleAsync(p => p.Id == known)
            : await fresh.Payments.OrderByDescending(p => p.CreatedAt).FirstAsync(p => p.Amount == 7m);

        var charged = await provider.Inner.GetStatusAsync(row.Id.ToString(), CancellationToken.None);
        charged.Outcome.Should().Be(ProviderOutcome.Charged, "the provider took the money");
        row.Status.Should().Be(PaymentStatus.Succeeded, "the money was taken, so the row must say so");
    }

    [Fact]
    public async Task GetAsync_returns_the_payment()
    {
        await using var db = NewContext();
        var service = new PaymentService(db, new AlwaysProvider(FakePaymentProvider.Behaviour.Success));
        var created = await service.CreateAsync(3m, "USD", CancellationToken.None);

        await using var db2 = NewContext();
        var found = await new PaymentService(db2, new AlwaysProvider(FakePaymentProvider.Behaviour.Success))
            .GetAsync(created.Id, CancellationToken.None);

        found.Should().NotBeNull();
        found!.Status.Should().Be(PaymentStatus.Succeeded);
    }

    [Fact]
    public async Task GetAsync_unknown_id_returns_null()
    {
        await using var db = NewContext();
        var service = new PaymentService(db, new AlwaysProvider(FakePaymentProvider.Behaviour.Success));

        var found = await service.GetAsync(Guid.NewGuid(), CancellationToken.None);

        found.Should().BeNull();
    }
}
