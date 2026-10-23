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

// M3: many requests with the SAME Idempotency-Key at the SAME time.
// Acceptance (README): 20 concurrent identical requests → exactly 1 provider call,
// 20 identical responses.
// The provider is slowed down so every request is guaranteed to overlap the charge.
// Needs: docker compose up -d
// Runs alone (no other test classes in parallel) so timing and contention are
// caused by these requests only.
[CollectionDefinition(nameof(ConcurrencyCollection), DisableParallelization = true)]
public class ConcurrencyCollection;

[Collection(nameof(ConcurrencyCollection))]
public class ConcurrentIdempotencyTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=payment_test;Username=postgres;Password=postgres";
    private const int N = 20;

    private readonly SlowCountingProvider _provider = new(TimeSpan.FromMilliseconds(200));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private sealed class SlowCountingProvider(TimeSpan delay) : IPaymentProvider
    {
        private int _charges;
        public FakePaymentProvider Inner { get; } =
            new() { DefaultBehaviour = FakePaymentProvider.Behaviour.Success };
        public int Charges => Volatile.Read(ref _charges);

        public async Task<ProviderResult> ChargeAsync(string reference, decimal amount, string currency, CancellationToken ct)
        {
            Interlocked.Increment(ref _charges);
            await Task.Delay(delay, CancellationToken.None);
            return await Inner.ChargeAsync(reference, amount, currency, ct);
        }

        public Task<ProviderResult> GetStatusAsync(string reference, CancellationToken ct) =>
            Inner.GetStatusAsync(reference, ct);
    }

    private sealed record Outcome(HttpStatusCode Status, string Body, string? Location);

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

    // Fires N requests with the same key, all released at the same instant.
    private async Task<Outcome[]> FireConcurrently(string key, string json)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, N).Select(async _ =>
        {
            await gate.Task;
            var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            var res = await _client.SendAsync(msg);
            return new Outcome(res.StatusCode, await res.Content.ReadAsStringAsync(), res.Headers.Location?.ToString());
        }).ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static string Describe(Outcome[] outcomes) =>
        string.Join(", ", outcomes.GroupBy(o => (int)o.Status).Select(g => $"{g.Count()}×{g.Key}"));

    [Fact]
    public async Task Concurrent_same_key_calls_the_provider_exactly_once()
    {
        var outcomes = await FireConcurrently("m3-" + Guid.NewGuid(), """{"amount": 30.00, "currency": "USD"}""");

        _provider.Charges.Should().Be(1, $"responses were: {Describe(outcomes)}");
    }

    [Fact]
    public async Task Concurrent_same_key_never_returns_a_5xx()
    {
        var outcomes = await FireConcurrently("m3-" + Guid.NewGuid(), """{"amount": 31.00, "currency": "USD"}""");

        outcomes.Should().NotContain(o => (int)o.Status >= 500, $"responses were: {Describe(outcomes)}");
    }

    [Fact]
    public async Task Concurrent_same_key_leaves_one_key_row_and_one_payment()
    {
        var key = "m3-" + Guid.NewGuid();
        var chargesBefore = _provider.Charges;

        await FireConcurrently(key, """{"amount": 32.00, "currency": "USD"}""");

        await using var db = NewContext();
        (await db.IdempotencyKeys.CountAsync(k => k.Key == key)).Should().Be(1);
        var row = await db.IdempotencyKeys.SingleAsync(k => k.Key == key);
        (await db.Payments.CountAsync(p => p.Id == row.PaymentId)).Should().Be(1);
        (_provider.Charges - chargesBefore).Should().Be(1, "every extra charge is an orphan payment the customer paid for");
    }

    // The README criterion (amended 2026-10-23, 409 design chosen):
    // exactly 1 provider call; every response is either the original 201 or 409; no 5xx.
    [Fact]
    public async Task Acceptance_20_concurrent_identical_requests_get_the_original_201_or_409()
    {
        var outcomes = await FireConcurrently("m3-" + Guid.NewGuid(), """{"amount": 33.00, "currency": "USD"}""");
        var why = $"responses were: {Describe(outcomes)}";

        outcomes.Should().OnlyContain(
            o => o.Status == HttpStatusCode.Created || o.Status == HttpStatusCode.Conflict, why);
        outcomes.Should().Contain(o => o.Status == HttpStatusCode.Created, why);

        // Every 201 (the original, plus any replay that landed after it finished) is the same response.
        var created = outcomes.Where(o => o.Status == HttpStatusCode.Created).ToArray();
        created.Select(o => o.Body).Distinct().Should().ContainSingle();
        created.Select(o => o.Location).Distinct().Should().ContainSingle();

        _provider.Charges.Should().Be(1);
    }

    // After the dust settles, a retry with the same key replays the original 201.
    [Fact]
    public async Task After_concurrent_burst_a_retry_replays_the_original_201()
    {
        var key = "m3-" + Guid.NewGuid();
        const string json = """{"amount": 35.00, "currency": "USD"}""";

        var outcomes = await FireConcurrently(key, json);
        var original = outcomes.First(o => o.Status == HttpStatusCode.Created);

        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        var retry = await _client.SendAsync(msg);

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        (await retry.Content.ReadAsStringAsync()).Should().Be(original.Body);
        _provider.Charges.Should().Be(1);
    }

    // Same key, DIFFERENT bodies, at the same time: the losers must get 422, not 409.
    [Fact]
    public async Task Concurrent_same_key_different_body_losers_get_422()
    {
        var key = "m3-" + Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, N).Select(async i =>
        {
            await gate.Task;
            var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
            {
                Content = new StringContent($$"""{"amount": {{36 + i}}.00, "currency": "USD"}""", System.Text.Encoding.UTF8, "application/json"),
            };
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            return (await _client.SendAsync(msg)).StatusCode;
        }).ToArray();

        gate.SetResult();
        var statuses = await Task.WhenAll(tasks);

        statuses.Count(s => s == HttpStatusCode.Created).Should().Be(1);
        statuses.Count(s => s == HttpStatusCode.UnprocessableEntity).Should().Be(N - 1);
        _provider.Charges.Should().Be(1);
    }

    // Different keys must NOT block each other: whatever serialises same-key
    // requests must not serialise the whole endpoint.
    [Fact]
    public async Task Concurrent_different_keys_each_charge_once()
    {
        // Warm up host + EF model + connection pool so startup isn't inside the timing.
        await _client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, N).Select(async i =>
        {
            await gate.Task;
            var msg = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
            {
                Content = new StringContent("""{"amount": 34.00, "currency": "USD"}""", System.Text.Encoding.UTF8, "application/json"),
            };
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", $"m3-diff-{Guid.NewGuid()}");
            return (await _client.SendAsync(msg)).StatusCode;
        }).ToArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        gate.SetResult();
        var statuses = await Task.WhenAll(tasks);
        sw.Stop();

        statuses.Should().AllSatisfy(s => s.Should().Be(HttpStatusCode.Created));
        _provider.Charges.Should().Be(N);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "20 × 200 ms in series would be 4 s; different keys must run in parallel");
    }
}
