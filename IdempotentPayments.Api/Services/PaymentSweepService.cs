namespace IdempotentPayments.Api.Services;

public sealed class PaymentSweepService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PaymentSweepService> _log;

    public PaymentSweepService(IServiceScopeFactory scopes, ILogger<PaymentSweepService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunPassAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunPassAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopes.CreateScope();
        var sweep = scope.ServiceProvider.GetRequiredService<PaymentSweep>();

        try
        {
            await sweep.RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host shutting down mid-pass; let the loop unwind normally
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sweep pass failed; will retry next tick.");
        }
    }
}
