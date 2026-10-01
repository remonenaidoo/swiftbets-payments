namespace SwiftBets.Payments.Domain;

/// <summary>Per-currency amount bounds and the withdrawal amount above which an operator must approve.</summary>
public sealed record CurrencyRules(long MinDeposit, long MaxDeposit, long MinWithdrawal, long MaxWithdrawal, long ApprovalThreshold);

public sealed class PaymentRules(IReadOnlyDictionary<string, CurrencyRules> currencies)
{
    public static PaymentRules Default { get; } = new(new Dictionary<string, CurrencyRules>(StringComparer.Ordinal)
    {
        ["ZAR"] = new(1_000, 5_000_000, 5_000, 10_000_000, 500_000),
        ["USD"] = new(100, 300_000, 500, 500_000, 30_000),
    });

    public PaymentError? CheckDeposit(long amount, string currency) =>
        !currencies.TryGetValue(currency, out var rules) ? PaymentError.CurrencyNotSupported
        : amount < rules.MinDeposit || amount > rules.MaxDeposit ? PaymentError.AmountOutOfRange
        : null;

    /// <summary>Withdrawals need a verified identity; the wallet separately refuses one blocked by a restriction.</summary>
    public PaymentError? CheckWithdrawal(long amount, string currency, bool kycVerified) =>
        !currencies.TryGetValue(currency, out var rules) ? PaymentError.CurrencyNotSupported
        : amount < rules.MinWithdrawal || amount > rules.MaxWithdrawal ? PaymentError.AmountOutOfRange
        : !kycVerified ? PaymentError.KycRequired
        : null;

    public bool NeedsApproval(long amount, string currency) =>
        currencies.TryGetValue(currency, out var rules) && amount > rules.ApprovalThreshold;
}
