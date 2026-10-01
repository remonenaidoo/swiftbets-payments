using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Infrastructure.Persistence;

internal sealed record DepositRow(
    Guid PaymentId, Guid UserId, Guid AccountId, long Amount, string Currency, string Provider, byte Status, string? ProviderReference, string? CheckoutUrl,
    string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public Deposit ToDomain() => new(PaymentId, UserId, AccountId, Amount, Currency, Provider, (DepositStatus)Status, ProviderReference, CheckoutUrl, FailureReason, CreatedAt, CompletedAt);
}

internal sealed record WithdrawalRow(
    Guid WithdrawalId, Guid UserId, Guid AccountId, long Amount, string Currency, string Provider, byte Status, Guid? ReservationId, bool RequiresApproval,
    bool HoldSettled, string? ProviderReference, string? DecidedBy, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public Withdrawal ToDomain() => new(
        WithdrawalId, UserId, AccountId, Amount, Currency, Provider, (WithdrawalStatus)Status, ReservationId, RequiresApproval, HoldSettled, ProviderReference, DecidedBy, Reason, CreatedAt, CompletedAt);
}

internal sealed record HistoryRow(string Kind, Guid Id, long Amount, string Currency, byte Status, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public PaymentHistoryItem ToItem() => new(
        Kind, Id, Amount, Currency, Kind == "deposit" ? Name((DepositStatus)Status) : Name((WithdrawalStatus)Status), Reason, CreatedAt, CompletedAt);

    private static string Name(Enum status) => char.ToLowerInvariant(status.ToString()[0]) + status.ToString()[1..];
}

internal sealed record SettledRow(string Reference, long Amount, string Currency, string? ProviderReference)
{
    public SettledMovement ToMovement() => new(Reference, Amount, Currency, ProviderReference);
}

internal sealed record RunRow(Guid RunId, string Provider, DateTime Day, DateTimeOffset CompletedAt);

internal sealed record DriftRow(byte Kind, string Reference, long? ProviderAmount, long? LedgerAmount, string Currency, string Detail)
{
    public PaymentDrift ToDomain() => new((DriftKind)Kind, Reference, ProviderAmount, LedgerAmount, Currency, Detail);
}
