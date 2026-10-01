namespace SwiftBets.Payments.Api.Endpoints;

/// <summary>Staff permissions in the <c>perm</c> claim; identity grants them to roles.</summary>
public static class PaymentPermissions
{
    public const string Read = "payments.read";

    public const string Approve = "payments.approve";
}
