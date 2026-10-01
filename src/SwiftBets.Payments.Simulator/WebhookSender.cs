using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SwiftBets.BuildingBlocks.Web.Webhooks;

namespace SwiftBets.Payments.Simulator;

public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    public string ApiKey { get; set; } = string.Empty;

    public string WebhookUrl { get; set; } = "http://payments:8080/webhooks/simulator";

    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>The address customers' browsers reach the simulator on, for checkout links.</summary>
    public string PublicUrl { get; set; } = "http://localhost:7140";

    public int TransferSeconds { get; set; } = 2;
}

/// <summary>Delivers signed webhooks in the background, retrying a failed delivery a few times like a real provider.</summary>
public sealed partial class WebhookSender(IHttpClientFactory http, IOptions<SimulatorOptions> options, TimeProvider time, ILogger<WebhookSender> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Channel<(string Type, SimPayment Payment)> _queue = Channel.CreateUnbounded<(string, SimPayment)>();

    public void Send(string type, SimPayment payment) => _queue.Writer.TryWrite((type, payment));

    /// <summary>Signs a delivery body; used by the sender and by tests that play the provider.</summary>
    public static (byte[] Body, string Signature) Sign(string secret, string type, SimPayment payment, DateTimeOffset at, string? eventId = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = eventId ?? $"evt_{Guid.NewGuid():N}",
            type,
            data = new { payment.Id, payment.Reference, payment.Amount, payment.Currency, payment.Reason },
        }, Json);
        return (body, new WebhookSignature([secret]).SignTimestamped(body, at));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (type, payment) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var (body, signature) = Sign(options.Value.WebhookSecret, type, payment, time.GetUtcNow());
            for (var attempt = 1; attempt <= 5 && !stoppingToken.IsCancellationRequested; attempt++)
            {
                if (await DeliverAsync(body, signature, stoppingToken))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), time, stoppingToken);
            }
        }
    }

    private async Task<bool> DeliverAsync(byte[] body, string signature, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.WebhookUrl) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json") { CharSet = Encoding.UTF8.WebName };
            request.Headers.Add("X-Simulator-Signature", signature);
            using var response = await http.CreateClient("webhooks").SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            LogDeliveryFailed(logger, ex.Message);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery failed: {Error}")]
    private static partial void LogDeliveryFailed(ILogger logger, string error);
}
