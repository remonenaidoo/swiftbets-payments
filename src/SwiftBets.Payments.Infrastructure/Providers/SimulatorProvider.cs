using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SwiftBets.BuildingBlocks.Web.Webhooks;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Infrastructure.Providers;

/// <summary>
/// The simulated provider (deposits and payouts). Its webhooks use the timestamped signature scheme and are refused
/// outside a five-minute window.
/// </summary>
public sealed class SimulatorProvider(HttpClient http, IOptions<SimulatorOptions> options, IOptions<PaymentsOptions> payments) : IPaymentProvider
{
    public const string ProviderName = "simulator";
    public const string SignatureHeader = "X-Simulator-Signature";
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => ProviderName;

    public bool SupportsWithdrawals => true;

    public async Task<ProviderCheckout> StartDepositAsync(Deposit deposit, string customerEmail, CancellationToken cancellationToken)
    {
        var started = await SendAsync<StartedPayment>(HttpMethod.Post, "v1/deposits", new
        {
            deposit.Reference,
            deposit.Amount,
            deposit.Currency,
            ReturnUrl = $"{payments.Value.ReturnUrl}?deposit={deposit.PaymentId}",
        }, cancellationToken);
        return new ProviderCheckout(started!.Id, started.CheckoutUrl!);
    }

    public async Task<ProviderEvent?> DepositStatusAsync(string reference, CancellationToken cancellationToken) =>
        await SendAsync<PaymentState>(HttpMethod.Get, $"v1/deposits/{reference}", null, cancellationToken) is { } state ? state.ToEvent("deposit") : null;

    public async Task<string> StartTransferAsync(Withdrawal withdrawal, CancellationToken cancellationToken) =>
        (await SendAsync<StartedPayment>(HttpMethod.Post, "v1/transfers", new { withdrawal.Reference, withdrawal.Amount, withdrawal.Currency }, cancellationToken))!.Id;

    public async Task<ProviderEvent?> TransferStatusAsync(string reference, CancellationToken cancellationToken) =>
        await SendAsync<PaymentState>(HttpMethod.Get, $"v1/transfers/{reference}", null, cancellationToken) is { } state ? state.ToEvent("transfer") : null;

    public async Task<IReadOnlyList<SettledMovement>> SettledAsync(DateOnly day, CancellationToken cancellationToken) =>
        [.. (await SendAsync<List<Settlement>>(HttpMethod.Get, $"v1/settlements?day={day:yyyy-MM-dd}", null, cancellationToken) ?? [])
            .Select(s => new SettledMovement(s.Reference, s.Amount, s.Currency, s.Id))];

    public Task RefundAsync(Deposit deposit, string reason, CancellationToken cancellationToken) =>
        SendAsync<PaymentState>(HttpMethod.Post, $"v1/deposits/{deposit.Reference}/refund", new { reason }, cancellationToken);

    public WebhookResult ReadWebhook(byte[] body, Func<string, string?> header, DateTimeOffset now)
    {
        if (options.Value.WebhookSecrets.Length == 0
            || new WebhookSignature(options.Value.WebhookSecrets).VerifyTimestamped(body, header(SignatureHeader), now, Tolerance) != WebhookVerdict.Valid)
        {
            return WebhookResult.Rejected;
        }

        var delivery = JsonSerializer.Deserialize<Delivery>(body, Json);
        if (delivery?.Data is not { } data || Kind(delivery.Type) is not { } kind)
        {
            return new WebhookResult(true, []);
        }

        return new WebhookResult(true, [new ProviderEvent(Name, delivery.Id, kind, data.Reference, data.Amount, data.Currency, data.Id, data.Reason)]);
    }

    private static ProviderEventKind? Kind(string? type) => type switch
    {
        "deposit.pending" => ProviderEventKind.DepositPending,
        "deposit.succeeded" => ProviderEventKind.DepositSucceeded,
        "deposit.failed" => ProviderEventKind.DepositFailed,
        "transfer.succeeded" => ProviderEventKind.TransferSucceeded,
        "transfer.failed" => ProviderEventKind.TransferFailed,
        _ => null,
    };

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
        request.Headers.Add("X-Api-Key", options.Value.ApiKey);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ProviderUnavailableException($"Simulator unreachable: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderUnavailableException($"Simulator answered {(int)response.StatusCode} for {method} {path}.");
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        }
    }

    private sealed record StartedPayment(string Id, string? CheckoutUrl);

    private sealed record PaymentState(string Id, string Reference, long Amount, string Currency, string Status, string? Reason)
    {
        // Open payments have no final state to report yet.
        public ProviderEvent? ToEvent(string kind) => (kind, Status) switch
        {
            ("deposit", "succeeded") => Event(ProviderEventKind.DepositSucceeded),
            ("deposit", "failed" or "refunded") => Event(ProviderEventKind.DepositFailed),
            ("transfer", "succeeded") => Event(ProviderEventKind.TransferSucceeded),
            ("transfer", "failed") => Event(ProviderEventKind.TransferFailed),
            _ => null,
        };

        private ProviderEvent Event(ProviderEventKind kind) =>
            new(ProviderName, string.Create(CultureInfo.InvariantCulture, $"status:{Id}:{Status}"), kind, Reference, Amount, Currency, Id, Reason);
    }

    private sealed record Settlement(string Id, string Reference, long Amount, string Currency);

    private sealed record Delivery(string Id, string Type, DeliveryData? Data);

    private sealed record DeliveryData(string Id, string Reference, long Amount, string Currency, string? Reason);
}
