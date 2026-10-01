namespace SwiftBets.Payments.Domain;

public enum ProviderEventKind
{
    DepositPending,
    DepositSucceeded,
    DepositFailed,
    TransferSucceeded,
    TransferFailed,
}

/// <summary>A provider's webhook or status answer in the service's own terms. Reference is ours (dep_… or wd_…).</summary>
public sealed record ProviderEvent(
    string Provider, string EventId, ProviderEventKind Kind, string Reference, long? Amount, string? Currency, string? ProviderReference, string? FailureReason);
