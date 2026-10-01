using System.ComponentModel.DataAnnotations;

namespace SwiftBets.Payments.Infrastructure;

public sealed class PaymentsOptions
{
    public const string SectionName = "Payments";

    /// <summary>Provider for deposits: simulator, or paystack (test mode).</summary>
    [Required]
    public string DepositProvider { get; set; } = "simulator";

    /// <summary>Provider for payouts; only the simulator pays out in v1.</summary>
    [Required]
    public string WithdrawalProvider { get; set; } = "simulator";

    /// <summary>Where the provider sends the customer after checkout; the payment id is appended as ?deposit=.</summary>
    [Required]
    public string ReturnUrl { get; set; } = "http://localhost:7100/account/wallet";

    /// <summary>Providers that need an email get user-id@this-domain; the customer's real address never leaves the platform.</summary>
    [Required]
    public string CustomerEmailDomain { get; set; } = "customers.swiftbets.example";

    [Range(10, 3600)]
    public int SweepSeconds { get; set; } = 60;

    /// <summary>The local (UTC+2) hour after which yesterday is reconciled.</summary>
    [Range(0, 23)]
    public int ReconcileAfterHour { get; set; } = 2;
}

public sealed class SimulatorOptions
{
    public const string SectionName = "Payments:Simulator";

    public string BaseUrl { get; set; } = "http://payments-simulator:8080";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Webhook signing secrets, newest first; more than one while a secret is rotated.</summary>
    public string[] WebhookSecrets { get; set; } = [];
}

public sealed class PaystackOptions
{
    public const string SectionName = "Payments:Paystack";

    public string BaseUrl { get; set; } = "https://api.paystack.co";

    /// <summary>The test-mode secret key (sk_test_…); it also signs webhooks.</summary>
    public string SecretKey { get; set; } = string.Empty;
}
