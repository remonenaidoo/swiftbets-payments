using Prometheus;

namespace SwiftBets.Payments.Infrastructure;

/// <summary>What the payments alerts read: webhook rejections, failing sweeps and reconciliation drift and freshness.</summary>
public static class PaymentsMetrics
{
    private static readonly Counter WebhooksRejectedCounter = Metrics.CreateCounter(
        "swiftbets_payments_webhooks_rejected_total", "Webhook deliveries refused for a bad, missing or expired signature, by provider.", new CounterConfiguration { LabelNames = ["provider"] });

    private static readonly Counter SweepFailuresCounter = Metrics.CreateCounter(
        "swiftbets_payments_sweep_failures_total", "Open-payment sweeps or reconciliation runs that threw.");

    private static readonly Gauge Drifts = Metrics.CreateGauge(
        "swiftbets_payments_reconciliation_drifts", "Drifts found by the latest reconciliation run, by provider.", new GaugeConfiguration { LabelNames = ["provider"] });

    private static readonly Gauge LastCompleted = Metrics.CreateGauge(
        "swiftbets_payments_reconciliation_last_completed_timestamp_seconds", "Unix time the latest reconciliation run completed, by provider.", new GaugeConfiguration { LabelNames = ["provider"] });

    public static void WebhookRejected(string provider) => WebhooksRejectedCounter.WithLabels(provider).Inc();

    public static void SweepFailed() => SweepFailuresCounter.Inc();

    public static void RunCompleted(string provider, DateTimeOffset at, int drifts)
    {
        Drifts.WithLabels(provider).Set(drifts);
        LastCompleted.WithLabels(provider).Set(at.ToUnixTimeSeconds());
    }
}
