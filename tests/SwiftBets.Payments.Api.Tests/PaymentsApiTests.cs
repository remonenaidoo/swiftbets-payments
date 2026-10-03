using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SwiftBets.BuildingBlocks.Testing;
using SwiftBets.BuildingBlocks.Web.Webhooks;

namespace SwiftBets.Payments.Api.Tests;

public sealed class PaymentsApiTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Webhooks_need_no_sign_in_but_a_bad_signature_is_refused()
    {
        await using var host = await PaymentsHost.StartAsync(sql);
        using var client = host.CreateClient();
        var body = Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"deposit.succeeded","data":{"id":"x","reference":"dep_00000000000000000000000000000000","amount":1,"currency":"ZAR"}}""");

        using var forged = await PostAsync(client, "/webhooks/simulator", body, new WebhookSignature(["wrong"]).SignTimestamped(body, DateTimeOffset.UtcNow));
        using var signed = await PostAsync(client, "/webhooks/simulator", body, new WebhookSignature([PaymentsHost.WebhookSecret]).SignTimestamped(body, DateTimeOffset.UtcNow));
        using var unknown = await PostAsync(client, "/webhooks/acme", body, "t=1,v1=00");

        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        signed.StatusCode.ShouldBe(HttpStatusCode.OK);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Payments_need_a_signed_in_customer()
    {
        await using var host = await PaymentsHost.StartAsync(sql);

        using var response = await host.CreateClient().PostAsJsonAsync("/me/deposits", new { amount = 10_000, currency = "ZAR" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unverified_customer_is_told_to_verify_before_withdrawing()
    {
        await using var host = await PaymentsHost.StartAsync(sql);
        using var client = host.ClientFor(Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync("/me/withdrawals", new { amount = 10_000, currency = "ZAR" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken))
            .RootElement.GetProperty("code").GetString().ShouldBe("kyc_required");
    }

    [Fact]
    public async Task An_amount_out_of_range_is_a_validation_error()
    {
        await using var host = await PaymentsHost.StartAsync(sql);
        using var client = host.ClientFor(Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync("/me/deposits", new { amount = 5, currency = "ZAR" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_approval_queue_is_for_staff_with_the_payments_permission()
    {
        await using var host = await PaymentsHost.StartAsync(sql);
        using var customer = host.ClientFor(Guid.NewGuid().ToString(), "Customer");

        using var response = await customer.GetAsync(new Uri("/admin/payments/withdrawals", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Steward_runs_the_open_payments_sweep_now_and_a_customer_cannot()
    {
        await using var host = await PaymentsHost.StartAsync(sql);
        using var steward = host.ClientFor("client:steward", "Service");
        using var customer = host.ClientFor(Guid.NewGuid().ToString(), "Customer");

        using var swept = await steward.PostAsync(new Uri("/admin/payments/open/sweep", UriKind.Relative), null, TestContext.Current.CancellationToken);
        using var refused = await customer.PostAsync(new Uri("/admin/payments/open/sweep", UriKind.Relative), null, TestContext.Current.CancellationToken);

        (swept.StatusCode, refused.StatusCode).ShouldBe((HttpStatusCode.OK, HttpStatusCode.Forbidden));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, byte[] body, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Simulator-Signature", signature);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
