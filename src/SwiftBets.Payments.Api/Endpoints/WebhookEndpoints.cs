using SwiftBets.BuildingBlocks.Web.Webhooks;
using SwiftBets.Payments.Application;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Infrastructure;

namespace SwiftBets.Payments.Api.Endpoints;

/// <summary>Provider webhooks. Anonymous: the signature is the authentication, checked against the exact bytes received.</summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhooks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/webhooks/{provider}", async (string provider, HttpContext context, WebhookHandler webhooks, PaymentsMetrics metrics, CancellationToken cancellationToken) =>
        {
            if (await context.Request.ReadRawBodyAsync() is not { } body)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            try
            {
                var outcome = await webhooks.ReceiveAsync(provider, body, name => context.Request.Headers[name].FirstOrDefault(), cancellationToken);
                if (outcome == WebhookOutcome.Unauthentic)
                {
                    metrics.WebhooksRejected.Add(1, new KeyValuePair<string, object?>("provider", provider));
                }

                return outcome switch
                {
                    WebhookOutcome.Accepted => Results.Ok(),
                    WebhookOutcome.UnknownProvider => Results.NotFound(),
                    _ => Results.Unauthorized(),
                };
            }
            catch (Exception ex) when (ex is WalletUnavailableException or ProviderUnavailableException)
            {
                // The provider delivers again; the work is keyed, so the retry is safe.
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();

        return endpoints;
    }
}
