using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Application.Ports;

public sealed record ProviderCheckout(string ProviderReference, string CheckoutUrl);

/// <summary>What a webhook delivery said, once its signature was checked. An inauthentic delivery carries no events.</summary>
public sealed record WebhookResult(bool Authentic, IReadOnlyList<ProviderEvent> Events)
{
    public static WebhookResult Rejected { get; } = new(false, []);
}

/// <summary>The provider could not be reached or answered with a server error; the call may be retried.</summary>
public sealed class ProviderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A payment provider. Our reference (dep_… or wd_…) is sent with every request, so a retried start is recognised
/// by the provider instead of charging or paying twice.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    bool SupportsWithdrawals { get; }

    Task<ProviderCheckout> StartDepositAsync(Deposit deposit, string customerEmail, CancellationToken cancellationToken);

    /// <summary>The deposit's final state at the provider, or null while it is still open or unknown there.</summary>
    Task<ProviderEvent?> DepositStatusAsync(string reference, CancellationToken cancellationToken);

    Task<string> StartTransferAsync(Withdrawal withdrawal, CancellationToken cancellationToken);

    Task<ProviderEvent?> TransferStatusAsync(string reference, CancellationToken cancellationToken);

    /// <summary>Everything the provider settled on a reconciliation day; payouts carry negative amounts.</summary>
    Task<IReadOnlyList<SettledMovement>> SettledAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>Gives back a deposit the wallet refused (a limit or restriction) after the customer paid.</summary>
    Task RefundAsync(Deposit deposit, string reason, CancellationToken cancellationToken);

    WebhookResult ReadWebhook(byte[] body, Func<string, string?> header, DateTimeOffset now);
}

public interface IPaymentProviders
{
    IPaymentProvider Deposits { get; }

    IPaymentProvider Withdrawals { get; }

    IReadOnlyList<IPaymentProvider> All { get; }

    IPaymentProvider? Find(string name);
}
