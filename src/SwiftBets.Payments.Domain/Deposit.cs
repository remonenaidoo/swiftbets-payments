namespace SwiftBets.Payments.Domain;

public enum DepositStatus : byte
{
    Created = 1,
    Pending = 2,
    Succeeded = 3,
    Failed = 4,
}

/// <summary>
/// A deposit intent. It only moves forward: Created → Pending → Succeeded or Failed. A provider event that arrives late,
/// twice or out of order changes nothing once the deposit is final, so the wallet is credited at most once.
/// </summary>
public sealed record Deposit(
    Guid PaymentId,
    Guid UserId,
    Guid AccountId,
    long Amount,
    string Currency,
    string Provider,
    DepositStatus Status,
    string? ProviderReference,
    string? CheckoutUrl,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>The reference the provider is given and echoes back in its webhooks.</summary>
    public string Reference => ReferenceFor(PaymentId);

    public bool IsFinal => Status is DepositStatus.Succeeded or DepositStatus.Failed;

    /// <summary>The wallet idempotency key: every attempt to credit this deposit uses it, so the credit lands once.</summary>
    public string WalletKey => $"deposit_{PaymentId:N}";

    public static string ReferenceFor(Guid paymentId) => $"dep_{paymentId:N}";

    public static Guid? IdFrom(string reference) =>
        reference.StartsWith("dep_", StringComparison.Ordinal) && Guid.TryParseExact(reference[4..], "N", out var id) ? id : null;

    public static Deposit Start(Guid userId, Guid accountId, long amount, string currency, string provider, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now), userId, accountId, amount, currency, provider, DepositStatus.Created, null, null, null, now, null);
}
