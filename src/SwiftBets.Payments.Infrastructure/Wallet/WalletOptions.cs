using System.ComponentModel.DataAnnotations;

namespace SwiftBets.Payments.Infrastructure.Wallet;

public sealed class WalletOptions
{
    public const string SectionName = "Wallet";

    [Required]
    public string GrpcAddress { get; set; } = string.Empty;

    [Range(1, 60)]
    public int DeadlineSeconds { get; set; } = 5;
}
