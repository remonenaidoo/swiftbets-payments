using SwiftBets.BuildingBlocks.Web;
using SwiftBets.Contracts.Errors;
using SwiftBets.Payments.Application;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;
using SwiftBets.Payments.Infrastructure;
using static SwiftBets.Payments.Api.Endpoints.PaymentResults;

namespace SwiftBets.Payments.Api.Endpoints;

/// <summary>The console's finance view: the approval queue, decisions, and reconciliation results.</summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminPayments(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/admin/payments");

        admin.MapGet("/withdrawals", async (string? status, IPaymentStore store, CancellationToken cancellationToken) =>
            Enum.TryParse<WithdrawalStatus>(status ?? nameof(WithdrawalStatus.AwaitingApproval), ignoreCase: true, out var parsed)
                ? Results.Ok((await store.WithdrawalsAsync(parsed, 200, cancellationToken)).Select(View))
                : Results.BadRequest())
            .RequireAuthorization(PaymentPermissions.Read);

        admin.MapPost("/withdrawals/{withdrawalId:guid}/approve", async (Guid withdrawalId, HttpContext context, WithdrawalHandler withdrawals, CancellationToken cancellationToken) =>
            await withdrawals.ApproveAsync(withdrawalId, Operator(context), cancellationToken) is var outcome && outcome.Error is { } error
                ? Problem(error, context)
                : Results.Ok(View(outcome.Value!)))
            .RequireAuthorization(PaymentPermissions.Approve);

        admin.MapPost("/withdrawals/{withdrawalId:guid}/reject", async (Guid withdrawalId, DecisionBody body, HttpContext context, WithdrawalHandler withdrawals, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 400)
            {
                return Error.Validation("reason_required", "Say why the withdrawal is rejected (up to 400 characters).").ToHttpResult(context);
            }

            var outcome = await withdrawals.RejectAsync(withdrawalId, Operator(context), body.Reason.Trim(), cancellationToken);
            return outcome.Error is { } error ? Problem(error, context) : Results.Ok(View(outcome.Value!));
        }).RequireAuthorization(PaymentPermissions.Approve);

        admin.MapGet("/reconciliation/{provider}/latest", async (string provider, IPaymentStore store, CancellationToken cancellationToken) =>
            await store.LatestRunAsync(provider, cancellationToken) is { } run ? Results.Ok(run) : Results.NotFound())
            .RequireAuthorization(PaymentPermissions.Read);

        admin.MapPost("/reconciliation/{provider}/run", async (string provider, DateOnly? day, ReconciliationHandler reconciliation, TimeProvider time, IPaymentProviders providers, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (providers.Find(provider) is null)
            {
                return Error.NotFound("provider_not_found", "No such provider.").ToHttpResult(context);
            }

            var run = await reconciliation.RunAsync(provider, day ?? PaymentCalendar.DayOf(time.GetUtcNow()).AddDays(-1), cancellationToken);
            PaymentsMetrics.RunCompleted(run.Provider, run.CompletedAt, run.Drifts.Count);
            return Results.Ok(run);
        }).RequireAuthorization(PaymentPermissions.Approve);

        return endpoints;
    }

    public sealed record DecisionBody(string? Reason);
}
