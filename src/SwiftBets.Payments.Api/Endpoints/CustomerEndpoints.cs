using Microsoft.Extensions.Options;
using SwiftBets.Payments.Application;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Infrastructure;
using static SwiftBets.Payments.Api.Endpoints.PaymentResults;

namespace SwiftBets.Payments.Api.Endpoints;

/// <summary>The signed-in customer's deposits, withdrawals and payment history, through the gateway's /api/me routes.</summary>
public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerPayments(this IEndpointRouteBuilder endpoints)
    {
        var me = endpoints.MapGroup("/me").RequireAuthorization();

        me.MapPost("/deposits", async (MoneyBody body, HttpContext context, DepositHandler deposits, IOptions<PaymentsOptions> options, CancellationToken cancellationToken) =>
        {
            var userId = UserId(context);
            var outcome = await deposits.StartAsync(userId, body.Amount, body.Currency ?? string.Empty, $"{userId:N}@{options.Value.CustomerEmailDomain}", cancellationToken);
            return outcome.Error is { } error ? Problem(error, context) : Results.Created($"/me/deposits/{outcome.Value!.PaymentId}", View(outcome.Value));
        });

        me.MapGet("/deposits/{paymentId:guid}", async (Guid paymentId, HttpContext context, IPaymentStore store, CancellationToken cancellationToken) =>
            await store.GetDepositAsync(paymentId, cancellationToken) is { } deposit && deposit.UserId == UserId(context)
                ? Results.Ok(View(deposit))
                : Problem(Domain.PaymentError.NotFound, context));

        me.MapPost("/withdrawals", async (MoneyBody body, HttpContext context, WithdrawalHandler withdrawals, CancellationToken cancellationToken) =>
        {
            var outcome = await withdrawals.RequestAsync(UserId(context), body.Amount, body.Currency ?? string.Empty, cancellationToken);
            return outcome.Error is { } error ? Problem(error, context) : Results.Created($"/me/withdrawals/{outcome.Value!.WithdrawalId}", View(outcome.Value));
        });

        me.MapGet("/payments", async (HttpContext context, IPaymentStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.HistoryAsync(UserId(context), 50, cancellationToken)));

        return endpoints;
    }

    public sealed record MoneyBody(long Amount, string? Currency);
}
