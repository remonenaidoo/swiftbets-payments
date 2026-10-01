using System.Diagnostics.Metrics;

namespace SwiftBets.Payments.Infrastructure;

/// <summary>Counters the payments alerts read (webhook rejections, sweep work, reconciliation drift and freshness).</summary>
public sealed class PaymentsMetrics : IDisposable
{
    public const string MeterName = "SwiftBets.Payments";
    private readonly Meter _meter = new(MeterName);
    private long _lastRunUnixSeconds;
    private int _lastDriftCount;

    public PaymentsMetrics()
    {
        WebhooksRejected = _meter.CreateCounter<long>("payments_webhooks_rejected_total", description: "Webhook deliveries refused for a bad or missing signature.");
        SweepFailures = _meter.CreateCounter<long>("payments_sweep_failures_total", description: "Sweeps or reconciliation runs that threw.");
        _meter.CreateObservableGauge("payments_reconciliation_last_run_timestamp_seconds", () => Interlocked.Read(ref _lastRunUnixSeconds), description: "When the last reconciliation completed.");
        _meter.CreateObservableGauge("payments_reconciliation_drifts", () => Volatile.Read(ref _lastDriftCount), description: "Drifts the last reconciliation found.");
    }

    public Counter<long> WebhooksRejected { get; }

    public Counter<long> SweepFailures { get; }

    public void RunCompleted(DateTimeOffset at, int drifts)
    {
        Interlocked.Exchange(ref _lastRunUnixSeconds, at.ToUnixTimeSeconds());
        Volatile.Write(ref _lastDriftCount, drifts);
    }

    public void Dispose() => _meter.Dispose();
}
