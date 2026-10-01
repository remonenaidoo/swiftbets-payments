namespace SwiftBets.Payments.Domain;

/// <summary>Why a payment request was refused; Code is the stable machine-readable form sent to clients.</summary>
public sealed record PaymentError(string Code, string Message)
{
    public static readonly PaymentError AmountOutOfRange = new("amount_out_of_range", "The amount is outside what can be deposited or withdrawn at once.");
    public static readonly PaymentError CurrencyNotSupported = new("currency_not_supported", "That currency is not supported.");
    public static readonly PaymentError KycRequired = new("kyc_required", "Verify your identity before you withdraw.");
    public static readonly PaymentError NotFound = new("payment_not_found", "No such payment.");
    public static readonly PaymentError InvalidState = new("invalid_state", "The payment cannot change from its current state.");
    public static readonly PaymentError ProviderUnavailable = new("provider_unavailable", "The payment provider could not be reached. Try again shortly.");
}
