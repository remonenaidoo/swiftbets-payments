using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftBets.Payments.Application;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Infrastructure.Workers;

/// <summary>
/// Reconciles yesterday (SA calendar) for every provider once the configured hour has passed, and again after a restart
/// if yesterday has no completed run yet. One replica doing it twice is harmless: a run only reads and reports.
/// </summary>
public sealed partial class DailyReconciliationWorker(
    IServiceProvider services, IOptions<PaymentsOptions> options, TimeProvider time, ILogger<DailyReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10), time);
        do
        {
            try
            {
                await RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PaymentsMetrics.SweepFailed();
                LogRunFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var local = time.GetUtcNow().ToOffset(PaymentCalendar.Offset);
        if (local.Hour < options.Value.ReconcileAfterHour)
        {
            return;
        }

        var yesterday = DateOnly.FromDateTime(local.Date).AddDays(-1);
        var store = services.GetRequiredService<IPaymentStore>();
        var handler = services.GetRequiredService<ReconciliationHandler>();
        foreach (var provider in services.GetRequiredService<IPaymentProviders>().All)
        {
            if ((await store.LatestRunAsync(provider.Name, cancellationToken))?.Day >= yesterday)
            {
                continue;
            }

            var run = await handler.RunAsync(provider.Name, yesterday, cancellationToken);
            PaymentsMetrics.RunCompleted(provider.Name, run.CompletedAt, run.Drifts.Count);
            LogRun(logger, provider.Name, yesterday, run.Drifts.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciled {Provider} for {Day}: {Drifts} drifts")]
    private static partial void LogRun(ILogger logger, string provider, DateOnly day, int drifts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Daily reconciliation failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
