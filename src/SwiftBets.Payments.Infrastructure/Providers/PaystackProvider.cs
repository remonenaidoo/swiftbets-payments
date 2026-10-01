using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SwiftBets.BuildingBlocks.Web.Webhooks;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Infrastructure.Providers;

/// <summary>
/// Paystack in test mode, for deposits only (D119). Webhooks are signed with HMAC-SHA512 of the body using the secret key
/// (x-paystack-signature). CI never calls Paystack: the adapter is tested against recorded responses.
/// </summary>
public sealed class PaystackProvider(HttpClient http, IOptions<PaystackOptions> options) : IPaymentProvider
{
    public const string ProviderName = "paystack";
    public const string SignatureHeader = "x-paystack-signature";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string Name => ProviderName;

    public bool SupportsWithdrawals => false;

    public async Task<ProviderCheckout> StartDepositAsync(Deposit deposit, string customerEmail, CancellationToken cancellationToken)
    {
        var started = await SendAsync<Envelope<Initialized>>(HttpMethod.Post, "transaction/initialize", new
        {
            email = customerEmail,
            amount = deposit.Amount,
            currency = deposit.Currency,
            reference = deposit.Reference,
        }, cancellationToken);
        return new ProviderCheckout(started!.Data!.AccessCode ?? deposit.Reference, started.Data.AuthorizationUrl);
    }

    public async Task<ProviderEvent?> DepositStatusAsync(string reference, CancellationToken cancellationToken) =>
        (await SendAsync<Envelope<Transaction>>(HttpMethod.Get, $"transaction/verify/{Uri.EscapeDataString(reference)}", null, cancellationToken))?.Data is { } transaction
            ? ToEvent($"verify:{transaction.Id}:{transaction.Status}", transaction)
            : null;

    public Task<string> StartTransferAsync(Withdrawal withdrawal, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Payouts go through the simulator in v1.");

    public Task<ProviderEvent?> TransferStatusAsync(string reference, CancellationToken cancellationToken) => Task.FromResult<ProviderEvent?>(null);

    public async Task<IReadOnlyList<SettledMovement>> SettledAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var (from, to) = PaymentCalendar.Bounds(day);
        var settled = new List<SettledMovement>();
        for (var page = 1; ; page++)
        {
            var path = string.Create(CultureInfo.InvariantCulture,
                $"transaction?status=success&perPage=100&page={page}&from={Uri.EscapeDataString(from.UtcDateTime.ToString("O"))}&to={Uri.EscapeDataString(to.UtcDateTime.ToString("O"))}");
            var list = await SendAsync<Envelope<List<Transaction>>>(HttpMethod.Get, path, null, cancellationToken);
            settled.AddRange((list?.Data ?? []).Select(t => new SettledMovement(t.Reference, t.Amount, t.Currency, t.Id.ToString(CultureInfo.InvariantCulture))));
            if (list?.Meta is not { } meta || page >= meta.PageCount)
            {
                return settled;
            }
        }
    }

    public Task RefundAsync(Deposit deposit, string reason, CancellationToken cancellationToken) =>
        SendAsync<Envelope<JsonElement>>(HttpMethod.Post, "refund", new { transaction = deposit.Reference, merchant_note = reason }, cancellationToken);

    public WebhookResult ReadWebhook(byte[] body, Func<string, string?> header, DateTimeOffset now)
    {
        if (options.Value.SecretKey.Length == 0
            || new WebhookSignature([options.Value.SecretKey], WebhookHash.Sha512).Verify(body, header(SignatureHeader)) != WebhookVerdict.Valid)
        {
            return WebhookResult.Rejected;
        }

        var delivery = JsonSerializer.Deserialize<Delivery>(body, Json);
        return delivery?.Data is { } transaction && delivery.Event == "charge.success" && ToEvent($"charge.success:{transaction.Id}", transaction) is { } success
            ? new WebhookResult(true, [success])
            : new WebhookResult(true, []);
    }

    private ProviderEvent? ToEvent(string eventId, Transaction transaction) => transaction.Status switch
    {
        "success" => new(Name, eventId, ProviderEventKind.DepositSucceeded, transaction.Reference, transaction.Amount, transaction.Currency, transaction.Id.ToString(CultureInfo.InvariantCulture), null),
        "failed" or "abandoned" or "reversed" => new(Name, eventId, ProviderEventKind.DepositFailed, transaction.Reference, transaction.Amount, transaction.Currency,
            transaction.Id.ToString(CultureInfo.InvariantCulture), transaction.GatewayResponse ?? transaction.Status),
        _ => null,
    };

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.SecretKey);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ProviderUnavailableException($"Paystack unreachable: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound || (response.StatusCode == HttpStatusCode.BadRequest && method == HttpMethod.Get))
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderUnavailableException($"Paystack answered {(int)response.StatusCode} for {method} {path}.");
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        }
    }

    private sealed record Envelope<T>(bool Status, string? Message, T? Data, Meta? Meta);

    // Paystack's meta block is camelCase while everything else is snake_case.
    private sealed record Meta([property: JsonPropertyName("pageCount")] int PageCount);

    private sealed record Initialized(string AuthorizationUrl, string? AccessCode, string Reference);

    private sealed record Transaction(long Id, string Status, string Reference, long Amount, string Currency, string? GatewayResponse);

    private sealed record Delivery(string Event, Transaction? Data);
}
