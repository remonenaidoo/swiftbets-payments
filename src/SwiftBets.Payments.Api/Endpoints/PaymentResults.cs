using SwiftBets.BuildingBlocks.Web;
using SwiftBets.Contracts.Errors;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Api.Endpoints;

internal static class PaymentResults
{
    public static IResult Problem(PaymentError error, HttpContext context) => (error.Code switch
    {
        "payment_not_found" => Error.NotFound(error.Code, error.Message),
        "provider_unavailable" => Error.Unavailable(error.Code, error.Message),
        "amount_out_of_range" or "currency_not_supported" => Error.Validation(error.Code, error.Message),
        "invalid_state" => Error.Conflict(error.Code, error.Message),
        _ => Error.BusinessRule(error.Code, error.Message),
    }).ToHttpResult(context);

    public static Guid UserId(HttpContext context) =>
        Guid.TryParse(context.User.FindFirst("sub")?.Value, out var id) ? id : throw new BadHttpRequestException("Token subject is not a user id.", 401);

    public static string Operator(HttpContext context) => context.User.FindFirst("sub")?.Value ?? "operator";

    public static object View(Deposit d) => new
    {
        d.PaymentId,
        d.Amount,
        d.Currency,
        status = Name(d.Status),
        d.CheckoutUrl,
        reason = d.FailureReason,
        d.CreatedAt,
        d.CompletedAt,
    };

    public static object View(Withdrawal w) => new
    {
        w.WithdrawalId,
        w.UserId,
        w.Amount,
        w.Currency,
        status = Name(w.Status),
        w.RequiresApproval,
        w.DecidedBy,
        reason = w.Reason,
        w.CreatedAt,
        w.CompletedAt,
    };

    private static string Name(Enum status) => char.ToLowerInvariant(status.ToString()[0]) + status.ToString()[1..];
}
