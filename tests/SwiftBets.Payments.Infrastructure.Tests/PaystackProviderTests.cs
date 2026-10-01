using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SwiftBets.Payments.Domain;
using SwiftBets.Payments.Infrastructure.Providers;

namespace SwiftBets.Payments.Infrastructure.Tests;

/// <summary>The Paystack test-mode adapter against responses recorded from its API; CI never calls Paystack.</summary>
public sealed class PaystackProviderTests : IDisposable
{
    private const string SecretKey = "sk_test_recorded";
    private static readonly Guid PaymentId = Guid.Parse("0199a000-0000-7000-8000-0000000000aa");
    private readonly Recorded _http = new();

    [Fact]
    public async Task Starting_a_deposit_sends_our_reference_and_amount_and_returns_the_checkout_link()
    {
        _http.Answer("POST transaction/initialize", "paystack-initialize.json");
        var deposit = Deposit.Start(Guid.NewGuid(), Guid.NewGuid(), 25_000, "ZAR", "paystack", DateTimeOffset.UnixEpoch) with { PaymentId = PaymentId };

        var checkout = await Provider().StartDepositAsync(deposit, "u@customers.example", TestContext.Current.CancellationToken);

        checkout.CheckoutUrl.ShouldBe("https://checkout.paystack.com/0peioxfhpn");
        _http.Bodies.ShouldHaveSingleItem().ShouldContain("\"reference\":\"dep_0199a0000000700080000000000000aa\"");
        _http.Bodies[0].ShouldContain("\"amount\":25000");
        _http.Authorizations.ShouldAllBe(a => a == $"Bearer {SecretKey}");
    }

    [Fact]
    public async Task Verification_maps_success_and_abandonment()
    {
        _http.Answer("GET transaction/verify/dep_0199a0000000700080000000000000aa", "paystack-verify-success.json");
        _http.Answer("GET transaction/verify/dep_0199a0000000700080000000000000ab", "paystack-verify-abandoned.json");

        var paid = await Provider().DepositStatusAsync("dep_0199a0000000700080000000000000aa", TestContext.Current.CancellationToken);
        var abandoned = await Provider().DepositStatusAsync("dep_0199a0000000700080000000000000ab", TestContext.Current.CancellationToken);

        (paid!.Kind, paid.Amount, paid.Currency, paid.ProviderReference).ShouldBe((ProviderEventKind.DepositSucceeded, (long?)25_000, "ZAR", "4099260516"));
        (abandoned!.Kind, abandoned.FailureReason).ShouldBe((ProviderEventKind.DepositFailed, "The transaction was not completed"));
    }

    [Fact]
    public async Task Settled_transactions_are_read_across_every_page()
    {
        _http.Answer("GET transaction?status=success&perPage=100&page=1", "paystack-transactions-page1.json");
        _http.Answer("GET transaction?status=success&perPage=100&page=2", "paystack-transactions-page2.json");

        var settled = await Provider().SettledAsync(new DateOnly(2026, 10, 1), TestContext.Current.CancellationToken);

        settled.Select(s => (s.Reference, s.Amount)).ShouldBe([("dep_0199a0000000700080000000000000aa", 25_000L), ("dep_0199a0000000700080000000000000ac", 10_000L)]);
    }

    [Fact]
    public void A_webhook_signed_with_the_secret_key_is_read_and_any_other_is_refused()
    {
        var body = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Recorded", "paystack-webhook-charge-success.json"));
        var signature = Convert.ToHexStringLower(HMACSHA512.HashData(Encoding.UTF8.GetBytes(SecretKey), body));

        var read = Provider().ReadWebhook(body, name => name == PaystackProvider.SignatureHeader ? signature : null, DateTimeOffset.UnixEpoch);
        var forged = Provider().ReadWebhook(body, _ => new string('0', 128), DateTimeOffset.UnixEpoch);

        read.Authentic.ShouldBeTrue();
        read.Events.ShouldHaveSingleItem().ShouldBe(new ProviderEvent("paystack", "charge.success:4099260516", ProviderEventKind.DepositSucceeded,
            "dep_0199a0000000700080000000000000aa", 25_000, "ZAR", "4099260516", null));
        forged.Authentic.ShouldBeFalse();
    }

    [Fact]
    public async Task A_server_error_is_unavailable_so_the_caller_can_retry()
    {
        _http.Fail(HttpStatusCode.BadGateway);
        var deposit = Deposit.Start(Guid.NewGuid(), Guid.NewGuid(), 25_000, "ZAR", "paystack", DateTimeOffset.UnixEpoch);

        await Should.ThrowAsync<Application.Ports.ProviderUnavailableException>(() => Provider().StartDepositAsync(deposit, "u@customers.example", TestContext.Current.CancellationToken));
    }

    public void Dispose() => _http.Dispose();

    private PaystackProvider Provider() =>
        new(new HttpClient(_http) { BaseAddress = new Uri("https://api.paystack.test/") }, Options.Create(new PaystackOptions { SecretKey = SecretKey }));

    /// <summary>Answers with recorded files keyed by method and path (query matched by prefix) and keeps what was sent.</summary>
    private sealed class Recorded : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _answers = [];
        private HttpStatusCode? _failure;

        public List<string> Bodies { get; } = [];

        public List<string?> Authorizations { get; } = [];

        public void Answer(string request, string file) => _answers[request] = file;

        public void Fail(HttpStatusCode status) => _failure = status;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            if (_failure is { } failure)
            {
                return new HttpResponseMessage(failure);
            }

            var key = $"{request.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}";
            var match = _answers.FirstOrDefault(a => key.StartsWith(a.Key, StringComparison.Ordinal));
            return match.Value is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Recorded", match.Value), cancellationToken), Encoding.UTF8, "application/json"),
                };
        }
    }
}
