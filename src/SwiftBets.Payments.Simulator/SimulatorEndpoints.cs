using System.Net;
using Microsoft.Extensions.Options;

namespace SwiftBets.Payments.Simulator;

/// <summary>
/// A simulated payment provider. The server API (/v1) needs the API key; the checkout pages are what the customer's
/// browser sees; /__faults and /__settlements let tests and the gate make it misbehave.
/// </summary>
public static class SimulatorEndpoints
{
    public static IEndpointRouteBuilder MapSimulator(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/v1").AddEndpointFilter(async (context, next) =>
        {
            var key = context.HttpContext.RequestServices.GetRequiredService<IOptions<SimulatorOptions>>().Value.ApiKey;
            return key.Length > 0 && context.HttpContext.Request.Headers["X-Api-Key"] != key ? Results.Unauthorized() : await next(context);
        });

        api.MapPost("/deposits", (StartBody body, SimulatorState state, WebhookSender webhooks, IOptions<SimulatorOptions> options) =>
        {
            var created = false;
            var payment = state.GetOrAdd(body.Reference, () =>
            {
                created = true;
                return new SimPayment($"sim_dep_{Guid.NewGuid():N}", body.Reference, false, body.Amount, body.Currency, body.ReturnUrl);
            });
            if (created && !state.Faults.ReverseOrder && !state.Faults.HoldWebhooks)
            {
                webhooks.Send("deposit.pending", payment);
            }

            return Results.Ok(new { payment.Id, CheckoutUrl = $"{options.Value.PublicUrl.TrimEnd('/')}/checkout/{payment.Reference}" });
        });

        api.MapGet("/deposits/{reference}", (string reference, SimulatorState state) =>
            state.Find(reference) is { IsTransfer: false } p ? Results.Ok(p.View) : Results.NotFound());

        api.MapPost("/deposits/{reference}/refund", (string reference, SimulatorState state) =>
        {
            if (state.Find(reference) is not { IsTransfer: false, Status: SimStatus.Succeeded } p)
            {
                return Results.NotFound();
            }

            p.Status = SimStatus.Refunded;
            return Results.Ok(p.View);
        });

        // Completes a checkout without a browser, for automated tests and the gate script.
        api.MapPost("/deposits/{reference}/complete", (string reference, string? outcome, SimulatorState state, WebhookSender webhooks, TimeProvider time) =>
            state.Find(reference) is { IsTransfer: false } p ? Results.Ok(Complete(p, outcome != "failed", state, webhooks, time).View) : Results.NotFound());

        api.MapPost("/transfers", (StartBody body, SimulatorState state, WebhookSender webhooks, TimeProvider time, IOptions<SimulatorOptions> options) =>
        {
            var created = false;
            var payment = state.GetOrAdd(body.Reference, () =>
            {
                created = true;
                return new SimPayment($"sim_trf_{Guid.NewGuid():N}", body.Reference, true, body.Amount, body.Currency, null);
            });
            if (created)
            {
                _ = SettleTransferLaterAsync(payment, state, webhooks, time, TimeSpan.FromSeconds(options.Value.TransferSeconds));
            }

            return Results.Ok(new { payment.Id, status = payment.Status.ToString().ToLowerInvariant() });
        });

        api.MapGet("/transfers/{reference}", (string reference, SimulatorState state) =>
            state.Find(reference) is { IsTransfer: true } p ? Results.Ok(p.View) : Results.NotFound());

        api.MapGet("/settlements", (DateOnly day, SimulatorState state) => Results.Ok(state.SettledOn(day)));

        endpoints.MapGet("/checkout/{reference}", (string reference, SimulatorState state) =>
            state.Find(reference) is { IsTransfer: false } p ? Results.Content(CheckoutPage(p), "text/html") : Results.NotFound());

        endpoints.MapPost("/checkout/{reference}/{choice}", (string reference, string choice, SimulatorState state, WebhookSender webhooks, TimeProvider time) =>
        {
            if (state.Find(reference) is not { IsTransfer: false } p || choice is not ("pay" or "decline"))
            {
                return Results.NotFound();
            }

            Complete(p, choice == "pay", state, webhooks, time);
            return p.ReturnUrl is { } back ? Results.Redirect(back) : Results.Content(CheckoutPage(p), "text/html");
        }).DisableAntiforgery();

        endpoints.MapPut("/__faults", (SimFaults faults, SimulatorState state) =>
        {
            state.Faults = faults;
            return Results.Ok(faults);
        });

        endpoints.MapPost("/__settlements", (InjectBody body, SimulatorState state, TimeProvider time) =>
        {
            state.Inject(body.Reference, body.Amount, body.Currency, body.At ?? time.GetUtcNow());
            return Results.Accepted();
        });

        return endpoints;
    }

    private static SimPayment Complete(SimPayment payment, bool paid, SimulatorState state, WebhookSender webhooks, TimeProvider time)
    {
        lock (payment)
        {
            if (payment.Status != SimStatus.Open)
            {
                return payment;
            }

            payment.Status = paid ? SimStatus.Succeeded : SimStatus.Failed;
            payment.Reason = paid ? null : "declined by the customer";
            payment.SettledAt = paid ? time.GetUtcNow() : null;
        }

        var faults = state.Faults;
        if (faults.HoldWebhooks)
        {
            return payment;
        }

        var type = paid ? "deposit.succeeded" : "deposit.failed";
        webhooks.Send(type, payment);
        if (faults.DuplicateWebhooks)
        {
            webhooks.Send(type, payment);
        }

        if (faults.ReverseOrder)
        {
            webhooks.Send("deposit.pending", payment);
        }

        return payment;
    }

    private static async Task SettleTransferLaterAsync(SimPayment payment, SimulatorState state, WebhookSender webhooks, TimeProvider time, TimeSpan after)
    {
        await Task.Delay(after, time);
        var failed = state.Faults.FailTransfers;
        payment.Status = failed ? SimStatus.Failed : SimStatus.Succeeded;
        payment.Reason = failed ? "beneficiary bank rejected the transfer" : null;
        payment.SettledAt = failed ? null : time.GetUtcNow();
        if (!state.Faults.HoldWebhooks)
        {
            webhooks.Send(failed ? "transfer.failed" : "transfer.succeeded", payment);
            if (state.Faults.DuplicateWebhooks)
            {
                webhooks.Send(failed ? "transfer.failed" : "transfer.succeeded", payment);
            }
        }
    }

    private static string CheckoutPage(SimPayment p)
    {
        var amount = WebUtility.HtmlEncode($"{p.Currency} {p.Amount / 100m:0.00}");
        var reference = WebUtility.HtmlEncode(p.Reference);
        var status = WebUtility.HtmlEncode(p.Status.ToString());
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Simulated checkout</title>
            <style>body{font-family:system-ui,sans-serif;max-width:28rem;margin:3rem auto;padding:0 1rem}button{font-size:1rem;padding:.6rem 1.2rem;margin-right:.5rem}</style>
            </head><body>
            <h1>Simulated checkout</h1>
            <p>Pay <strong>{{amount}}</strong> to SwiftBets.</p>
            <p>Reference {{reference}} · status {{status}}</p>
            <form method="post" action="/checkout/{{reference}}/pay" style="display:inline"><button type="submit">Pay</button></form>
            <form method="post" action="/checkout/{{reference}}/decline" style="display:inline"><button type="submit">Decline</button></form>
            <p><small>This is a test provider. No real money moves.</small></p>
            </body></html>
            """;
    }

    public sealed record StartBody(string Reference, long Amount, string Currency, string? ReturnUrl);

    public sealed record InjectBody(string Reference, long Amount, string Currency, DateTimeOffset? At);
}
