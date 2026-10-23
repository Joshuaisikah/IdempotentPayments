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

// M2: Idempotency-Key through the real HTTP pipeline (sequential requests only;
// concurrency is M3). Uses Postgres from docker-compose, database "payment_test".
// Needs: docker compose up -d
public class IdempotencyEndpointTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";

    private readonly CountingProvider _provider = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    // Wraps the real fake provider and counts every charge attempt.
    private sealed class CountingProvider : IPaymentProvider
    {
        private int _charges;
        public FakePaymentProvider Inner { get; } =
            new() { DefaultBehaviour = FakePaymentProvider.Behaviour.Success };
        public int Charges => Volatile.Read(ref _charges);

        public Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            Interlocked.Increment(ref _charges);
            return Inner.ChargeAsync(reference, amount, currency, ct);
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

    private static string NewKey() => "test-" + Guid.NewGuid();

    // Raw JSON so tests control exact number formatting (10.5 vs 10.50).
    private Task<HttpResponseMessage> Post(string? key, string json)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (key is not null)
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return _client.SendAsync(msg);
    }

    private static string Body(string amount, string currency) =>
        $$"""{"amount": {{amount}}, "currency": "{{currency}}"}""";

    // ── a) missing / bad key ───────────────────────────────────────────────

    [Fact]
    public async Task Missing_key_is_400_and_does_not_charge()
    {
        var res = await Post(null, Body("10.00", "USD"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _provider.Charges.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_key_is_400_and_does_not_charge(string key)
    {
        var res = await Post(key, Body("10.00", "USD"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _provider.Charges.Should().Be(0);
    }

    [Fact]
    public async Task Key_longer_than_255_is_400_and_does_not_charge()
    {
        var res = await Post(new string('k', 256), Body("10.00", "USD"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _provider.Charges.Should().Be(0);
    }

    [Fact]
    public async Task Key_of_exactly_255_is_accepted()
    {
        var key = (NewKey() + new string('k', 255))[..255];

        var res = await Post(key, Body("10.00", "USD"));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── b) validation errors are not cached ────────────────────────────────

    [Fact]
    public async Task Validation_error_is_400_not_cached_so_same_key_can_be_fixed_and_retried()
    {
        var key = NewKey();

        var bad = await Post(key, Body("0", "USD"));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using (var db = NewContext())
            (await db.IdempotencyKeys.AnyAsync(k => k.Key == key)).Should().BeFalse();

        var good = await Post(key, Body("10.00", "USD"));
        good.StatusCode.Should().Be(HttpStatusCode.Created);
        _provider.Charges.Should().Be(1);
    }

    // ── first request ──────────────────────────────────────────────────────

    [Fact]
    public async Task First_request_is_201_with_Location_and_stores_the_key()
    {
        var key = NewKey();

        var res = await Post(key, Body("25.00", "USD"));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("id").GetGuid();
        res.Headers.Location!.ToString().Should().Be($"/api/payments/{id}");

        await using var db = NewContext();
        var row = await db.IdempotencyKeys.SingleAsync(k => k.Key == key);
        row.PaymentId.Should().Be(id);
        row.ResponseStatusCode.Should().Be(201);
        row.RequestHash.Should().HaveLength(64);
        row.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        row.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ── c) sequential replay ───────────────────────────────────────────────

    [Fact]
    public async Task Replay_returns_identical_status_body_and_Location_and_charges_once()
    {
        var key = NewKey();

        var first = await Post(key, Body("42.00", "USD"));
        var second = await Post(key, Body("42.00", "USD"));

        second.StatusCode.Should().Be(first.StatusCode);
        (await second.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        second.Headers.Location.Should().Be(first.Headers.Location);
        second.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        _provider.Charges.Should().Be(1);
    }

    [Fact]
    public async Task Replay_creates_no_second_payment_row()
    {
        var key = NewKey();

        var first = await Post(key, Body("43.00", "USD"));
        await Post(key, Body("43.00", "USD"));
        await Post(key, Body("43.00", "USD"));

        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var db = NewContext();
        (await db.IdempotencyKeys.CountAsync(k => k.Key == key)).Should().Be(1);
        (await db.Payments.CountAsync(p => p.Id == id)).Should().Be(1);
        _provider.Charges.Should().Be(1);
    }

    [Fact]
    public async Task Same_amount_with_different_scale_is_a_replay_not_a_422()
    {
        var key = NewKey();

        var first = await Post(key, Body("10.5", "USD"));
        var second = await Post(key, Body("10.50", "USD"));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        _provider.Charges.Should().Be(1);
    }

    [Fact]
    public async Task Same_currency_different_case_is_a_replay_not_a_422()
    {
        var key = NewKey();

        await Post(key, Body("11.00", "usd"));
        var second = await Post(key, Body("11.00", "USD"));

        second.StatusCode.Should().Be(HttpStatusCode.Created);
        _provider.Charges.Should().Be(1);
    }

    [Fact]
    public async Task Declined_payment_is_replayed_as_the_same_declined_response_not_recharged()
    {
        _provider.Inner.DefaultBehaviour = FakePaymentProvider.Behaviour.Decline;
        var key = NewKey();

        var first = await Post(key, Body("12.00", "USD"));
        var second = await Post(key, Body("12.00", "USD"));

        var firstBody = await first.Content.ReadAsStringAsync();
        JsonDocument.Parse(firstBody).RootElement.GetProperty("status").GetString().Should().Be("Failed");
        (await second.Content.ReadAsStringAsync()).Should().Be(firstBody);
        second.StatusCode.Should().Be(first.StatusCode);
        _provider.Charges.Should().Be(1);
    }

    [Fact]
    public async Task Different_keys_with_the_same_body_are_two_payments()
    {
        await Post(NewKey(), Body("13.00", "USD"));
        await Post(NewKey(), Body("13.00", "USD"));

        _provider.Charges.Should().Be(2);
    }

    // ── d) same key, different request → 422 ───────────────────────────────

    [Theory]
    [InlineData("50.00", "USD", "50.01", "USD")]
    [InlineData("50.00", "USD", "50.00", "EUR")]
    public async Task Same_key_with_different_request_is_422_and_does_not_charge_again(
        string a1, string c1, string a2, string c2)
    {
        var key = NewKey();

        await Post(key, Body(a1, c1));
        var second = await Post(key, Body(a2, c2));

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _provider.Charges.Should().Be(1);
    }

    [Fact]
    public async Task A_422_does_not_overwrite_the_stored_response()
    {
        var key = NewKey();

        var first = await Post(key, Body("60.00", "USD"));
        await Post(key, Body("99.00", "USD"));
        var third = await Post(key, Body("60.00", "USD"));

        third.StatusCode.Should().Be(HttpStatusCode.Created);
        (await third.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        _provider.Charges.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_body_with_a_used_key_is_400_not_422()
    {
        var key = NewKey();

        await Post(key, Body("70.00", "USD"));
        var res = await Post(key, Body("-1", "USD"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── GET still works for the created payment ────────────────────────────

    [Fact]
    public async Task Location_from_replay_resolves_with_GET()
    {
        var key = NewKey();
        await Post(key, Body("80.00", "USD"));
        var replay = await Post(key, Body("80.00", "USD"));

        var get = await _client.GetAsync(replay.Headers.Location);

        get.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
