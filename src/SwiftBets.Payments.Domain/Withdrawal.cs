namespace SwiftBets.Payments.Domain;

public enum WithdrawalStatus : byte
{
    /// <summary>Recorded; the wallet hold has not been confirmed yet.</summary>
    Requested = 1,
    AwaitingApproval = 2,
    Approved = 3,
    Submitted = 4,
    Paid = 5,
    Failed = 6,
    Rejected = 7,
}

/// <summary>
/// A withdrawal. The amount is held in the wallet first; above the approval threshold an operator decides; then the
/// provider pays it out. Status changes before the wallet is told, and HoldSettled records that the wallet hold was
/// paid out or returned, so a crash in between is finished by the reconciler rather than lost.
/// </summary>
public sealed record Withdrawal(
    Guid WithdrawalId,
    Guid UserId,
    Guid AccountId,
    long Amount,
    string Currency,
    string Provider,
    WithdrawalStatus Status,
    Guid? ReservationId,
    bool RequiresApproval,
    bool HoldSettled,
    string? ProviderReference,
    string? DecidedBy,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public string Reference => ReferenceFor(WithdrawalId);

    public bool IsFinal => Status is WithdrawalStatus.Paid or WithdrawalStatus.Failed or WithdrawalStatus.Rejected;

    public string HoldKey => $"withdrawal_{WithdrawalId:N}_hold";

    public string PayKey => $"withdrawal_{WithdrawalId:N}_paid";

    public string ReturnKey => $"withdrawal_{WithdrawalId:N}_returned";

    public static string ReferenceFor(Guid withdrawalId) => $"wd_{withdrawalId:N}";

    public static Guid? IdFrom(string reference) =>
        reference.StartsWith("wd_", StringComparison.Ordinal) && Guid.TryParseExact(reference[3..], "N", out var id) ? id : null;

    public static Withdrawal Request(Guid userId, Guid accountId, long amount, string currency, string provider, bool requiresApproval, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now), userId, accountId, amount, currency, provider, WithdrawalStatus.Requested, null, requiresApproval, false, null, null, null, now, null);

    /// <summary>The status once the hold is in place: an operator looks at large amounts first.</summary>
    public WithdrawalStatus AfterHold => RequiresApproval ? WithdrawalStatus.AwaitingApproval : WithdrawalStatus.Approved;

    /// <summary>Whether the hold, once the status is final, goes to the provider (paid) or back to the customer.</summary>
    public bool PaysOut => Status == WithdrawalStatus.Paid;
}
