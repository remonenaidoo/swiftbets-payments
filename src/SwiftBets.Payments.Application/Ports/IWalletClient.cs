namespace SwiftBets.Payments.Application.Ports;

/// <summary>A wallet answer: success with the id it produced (account, reservation or posting), or the wallet's refusal as a snake_case code.</summary>
public sealed record WalletResult(bool Succeeded, Guid? Id, string? FailureCode, string? Message)
{
    public static WalletResult Ok(Guid? id = null) => new(true, id, null, null);

    public static WalletResult Refused(string code, string message) => new(false, null, code, message);
}

/// <summary>The wallet did not answer; the same keyed call can be repeated safely.</summary>
public sealed class WalletUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The wallet's money API. Every mutation is keyed, so repeating one after a timeout never moves money twice.</summary>
public interface IWalletClient
{
    Task<WalletResult> OpenAccountAsync(Guid userId, string currency, CancellationToken cancellationToken);

    Task<WalletResult> DepositAsync(string key, Guid accountId, long amount, string currency, string reference, CancellationToken cancellationToken);

    Task<WalletResult> HoldWithdrawalAsync(string key, Guid accountId, long amount, string currency, string reference, CancellationToken cancellationToken);

    Task<WalletResult> CompleteWithdrawalAsync(string key, Guid reservationId, CancellationToken cancellationToken);

    Task<WalletResult> ReturnWithdrawalAsync(string key, Guid reservationId, CancellationToken cancellationToken);
}

/// <summary>Compliance's view of a customer, read from its compacted snapshot.</summary>
public interface ICustomerStatus
{
    bool IsKycVerified(Guid userId);
}
