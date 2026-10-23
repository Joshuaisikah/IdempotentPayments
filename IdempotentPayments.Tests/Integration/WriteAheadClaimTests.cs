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

// M4 step 1: the claim row carries the payment reference BEFORE the charge,
// so a later takeover of a dead claim knows which reference to ask the provider about.
// Needs: docker compose up -d
public class WriteAheadClaimTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";

    private readonly SpyProvider _provider = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    // What the database looked like at the instant the provider was called.
    private sealed record Snapshot(string Reference, IdempotencyKey? Claim, Payment? Payment);

    // Before charging, reads the DB through its own connection and records what a
    // takeover would see if this request died right now.
    private sealed class SpyProvider : IPaymentProvider
    {
        public FakePaymentProvider Inner { get; } =
            new() { DefaultBehaviour = FakePaymentProvider.Behaviour.Success };
        public List<Snapshot> Snapshots { get; } = new();

        public async Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            var id = Guid.Parse(reference);
            await using (var db = NewContext())
            {
                var claim = await db.IdempotencyKeys.AsNoTracking().SingleOrDefaultAsync(k => k.PaymentId == id);
                var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
                lock (Snapshots)
                    Snapshots.Add(new Snapshot(reference, claim, payment));
            }
            return await Inner.ChargeAsync(reference, amount, currency, ct);
        }

        public Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct) =>
            Inner.GetStatusAsync(reference, ct);
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

    private Task<HttpResponseMessage> Post(string key, string json)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return _client.SendAsync(msg);
    }

    // ── service ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Service_uses_the_given_id_and_sends_it_as_the_provider_reference()
    {
        var id = Guid.NewGuid();
        await using var db = NewContext();
        var service = new PaymentService(db, _provider, TestDatabase.Options);

        var created = await service.CreateAsync(id, 20.00m, "USD", CancellationToken.None);

        created.Id.Should().Be(id);
        _provider.Snapshots.Should().ContainSingle().Which.Reference.Should().Be(id.ToString());
    }

    // ── endpoint: what a takeover would see mid-charge ─────────────────────

    [Fact]
    public async Task At_the_moment_of_the_charge_the_claim_already_holds_the_reference_and_no_response()
    {
        var key = "m4-" + Guid.NewGuid();

        var res = await Post(key, """{"amount": 21.00, "currency": "USD"}""");
        res.StatusCode.Should().Be(HttpStatusCode.Created);

        var snap = _provider.Snapshots.Should().ContainSingle().Subject;
        snap.Claim.Should().NotBeNull("the claim must be committed before the provider is called");
        snap.Claim!.Key.Should().Be(key);
        snap.Claim.PaymentId.Should().Be(Guid.Parse(snap.Reference));
        snap.Claim.ResponseBody.Should().BeNull();
        snap.Claim.ResponseStatusCode.Should().BeNull();
    }

    [Fact]
    public async Task At_the_moment_of_the_charge_the_payment_row_is_Processing_write_ahead()
    {
        await Post("m4-" + Guid.NewGuid(), """{"amount": 22.00, "currency": "USD"}""");

        var snap = _provider.Snapshots.Should().ContainSingle().Subject;
        snap.Payment.Should().NotBeNull();
        snap.Payment!.Status.Should().Be(PaymentStatus.Processing);
    }

    [Fact]
    public async Task After_completion_claim_payment_response_and_provider_reference_all_agree()
    {
        var key = "m4-" + Guid.NewGuid();

        var res = await Post(key, """{"amount": 23.00, "currency": "USD"}""");
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using var db = NewContext();
        var claim = await db.IdempotencyKeys.SingleAsync(k => k.Key == key);
        claim.PaymentId.Should().Be(id);
        claim.ResponseStatusCode.Should().Be(201);
        _provider.Snapshots.Single().Reference.Should().Be(id.ToString());
    }

    [Fact]
    public async Task Each_new_key_gets_its_own_payment_id()
    {
        await Post("m4-" + Guid.NewGuid(), """{"amount": 24.00, "currency": "USD"}""");
        await Post("m4-" + Guid.NewGuid(), """{"amount": 24.00, "currency": "USD"}""");

        _provider.Snapshots.Select(s => s.Reference).Distinct().Should().HaveCount(2);
    }

    // ── the dead-claim state M4 has to recover from ───────────────────────

    // Charge goes through, response is lost (flavour 2). The request dies with a 5xx.
    // This test pins down what is LEFT BEHIND, i.e. the input to the takeover logic.
    // It does not test recovery itself (step 2).
    [Fact]
    public async Task Timeout_after_charge_leaves_a_recoverable_claim_with_the_charged_reference()
    {
        var key = "m4-" + Guid.NewGuid();
        _provider.Inner.DefaultBehaviour = FakePaymentProvider.Behaviour.TimeoutAfterCharge;

        var res = await Post(key, """{"amount": 25.00, "currency": "USD"}""");
        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(500);

        await using var db = NewContext();
        var claim = await db.IdempotencyKeys.SingleAsync(k => k.Key == key);
        claim.ResponseBody.Should().BeNull("the request died before completing");
        claim.PaymentId.Should().NotBeNull("a takeover needs the reference");

        var payment = await db.Payments.SingleAsync(p => p.Id == claim.PaymentId);
        payment.Status.Should().Be(PaymentStatus.Processing);

        var atProvider = await _provider.Inner.GetStatusAsync(claim.PaymentId!.Value.ToString(), CancellationToken.None);
        atProvider.Outcome.Should().Be(ProviderOutcome.Charged, "the money moved even though the request failed");
    }
}
