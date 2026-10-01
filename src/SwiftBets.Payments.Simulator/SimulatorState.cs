using System.Collections.Concurrent;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Simulator;

public enum SimStatus
{
    Open,
    Processing,
    Succeeded,
    Failed,
    Refunded,
}

/// <summary>A payment as the simulated provider holds it. Amount is positive; payouts are reported negative in settlements.</summary>
public sealed class SimPayment(string id, string reference, bool isTransfer, long amount, string currency, string? returnUrl)
{
    public string Id { get; } = id;

    public string Reference { get; } = reference;

    public bool IsTransfer { get; } = isTransfer;

    public long Amount { get; } = amount;

    public string Currency { get; } = currency;

    public string? ReturnUrl { get; } = returnUrl;

    public SimStatus Status { get; set; } = isTransfer ? SimStatus.Processing : SimStatus.Open;

    public string? Reason { get; set; }

    public DateTimeOffset? SettledAt { get; set; }

    public object View => new { Id, Reference, Amount, Currency, status = Status.ToString().ToLowerInvariant(), reason = Reason };
}

/// <summary>Switches that make the simulator misbehave the way real providers do, for tests and the E3 gate.</summary>
public sealed record SimFaults(bool DuplicateWebhooks = false, bool ReverseOrder = false, bool FailTransfers = false, bool HoldWebhooks = false);

/// <summary>In memory: a restart forgets everything, which is acceptable for a simulator.</summary>
public sealed class SimulatorState
{
    private readonly ConcurrentDictionary<string, SimPayment> _byReference = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<(string Reference, long Amount, string Currency, DateTimeOffset At)> _injected = [];

    public SimFaults Faults { get; set; } = new();

    public SimPayment GetOrAdd(string reference, Func<SimPayment> create) => _byReference.GetOrAdd(reference, _ => create());

    public SimPayment? Find(string reference) => _byReference.GetValueOrDefault(reference);

    /// <summary>A settled movement that never happened through this platform, to give the reconciliation something to find.</summary>
    public void Inject(string reference, long amount, string currency, DateTimeOffset at) => _injected.Add((reference, amount, currency, at));

    public IEnumerable<object> SettledOn(DateOnly day)
    {
        var (from, to) = PaymentCalendar.Bounds(day);
        foreach (var p in _byReference.Values.Where(p => p.Status == SimStatus.Succeeded && p.SettledAt >= from && p.SettledAt < to))
        {
            yield return new { p.Id, p.Reference, Amount = p.IsTransfer ? -p.Amount : p.Amount, p.Currency };
        }

        foreach (var i in _injected.Where(i => i.At >= from && i.At < to))
        {
            yield return new { Id = $"sim_inj_{i.Reference}", i.Reference, i.Amount, i.Currency };
        }
    }
}
