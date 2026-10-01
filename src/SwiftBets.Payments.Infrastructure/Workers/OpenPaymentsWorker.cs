using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftBets.Payments.Application;

namespace SwiftBets.Payments.Infrastructure.Workers;

/// <summary>Sweeps payments a webhook never finished, on a fixed interval.</summary>
public sealed partial class OpenPaymentsWorker(IServiceProvider services, IOptions<PaymentsOptions> options, PaymentsMetrics metrics, ILogger<OpenPaymentsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SweepSeconds));
        do
        {
            try
            {
                await services.GetRequiredService<OpenPaymentsHandler>().SweepAsync(100, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                metrics.SweepFailures.Add(1);
                LogSweepFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Open-payments sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
