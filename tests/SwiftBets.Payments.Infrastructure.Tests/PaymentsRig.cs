extern alias migrator;

using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SwiftBets.BuildingBlocks.Messaging;
using SwiftBets.BuildingBlocks.Outbox;
using SwiftBets.BuildingBlocks.Persistence;
using SwiftBets.BuildingBlocks.Testing;
using SwiftBets.Contracts.Messaging;
using SwiftBets.Payments.Application;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;
using SwiftBets.Payments.Infrastructure.Persistence;
using SwiftBets.Payments.Infrastructure.Providers;
using SwiftBets.Payments.Simulator;

namespace SwiftBets.Payments.Infrastructure.Tests;

/// <summary>
/// Payments wired for real against SQL Server and the simulated provider running in-process. The simulator's webhooks
/// are delivered straight into this rig's webhook handler, signed exactly as they would be over the network.
/// </summary>
public sealed class PaymentsRig : IAsyncDisposable
{
    public const string WebhookSecret = "whsec_test";
    private readonly WebApplicationFactory<SimulatorState> _simulator;

    private PaymentsRig(string connectionString)
    {
        ConnectionString = connectionString;
        var outbox = new SqlServerOutbox(Options.Create(new KafkaOptions { BootstrapServers = "unused:9092", Environment = "test", ClientId = "payments-tests" }), Time);
        Store = new SqlPaymentStore(new SqlServerConnectionFactory(connectionString), outbox);

        _simulator = new WebApplicationFactory<SimulatorState>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Simulator:ApiKey", "sim-key");
            host.UseSetting("Simulator:WebhookSecret", WebhookSecret);
            host.UseSetting("Simulator:PublicUrl", "http://simulator.test");
            host.UseSetting("Simulator:TransferSeconds", "0");
            host.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(Time);
                services.AddHttpClient("webhooks").ConfigurePrimaryHttpMessageHandler(() => new DeliverInProcess(this));
            });
        });

        var payments = Options.Create(new PaymentsOptions { ReturnUrl = "http://site.test/account/wallet" });
        Provider = new SimulatorProvider(_simulator.CreateClient(), Options.Create(new SwiftBets.Payments.Infrastructure.SimulatorOptions { ApiKey = "sim-key", WebhookSecrets = [WebhookSecret] }), payments);
        var providers = new PaymentProviders([Provider], payments);
        Deposits = new DepositHandler(Store, providers, Wallet, PaymentRules.Default, Time, NullLogger<DepositHandler>.Instance);
        Withdrawals = new WithdrawalHandler(Store, providers, Wallet, Customers, PaymentRules.Default, Time, NullLogger<WithdrawalHandler>.Instance);
        Webhooks = new WebhookHandler(providers, Store, Deposits, Withdrawals, Time);
        Sweep = new OpenPaymentsHandler(Store, providers, Deposits, Withdrawals, Time, NullLogger<OpenPaymentsHandler>.Instance);
        Reconciliation = new ReconciliationHandler(Store, providers, Time);
    }

    public string ConnectionString { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));

    public SqlPaymentStore Store { get; }

    public FakeWallet Wallet { get; } = new();

    public FakeCustomers Customers { get; } = new();

    public SimulatorProvider Provider { get; }

    public DepositHandler Deposits { get; }

    public WithdrawalHandler Withdrawals { get; }

    public WebhookHandler Webhooks { get; }

    public OpenPaymentsHandler Sweep { get; }

    public ReconciliationHandler Reconciliation { get; }

    /// <summary>Webhook deliveries the rig has answered, in order, with what it answered.</summary>
    public List<HttpStatusCode> Deliveries { get; } = [];

    public static async Task<PaymentsRig> CreateAsync(SqlServerFixture sql)
    {
        var connectionString = await sql.CreateDatabaseAsync("pay_" + Guid.NewGuid().ToString("N")[..10]);
        (await MigrateAsync(connectionString)).ShouldBe(0);
        return new PaymentsRig(connectionString);
    }

    public static async Task<int> MigrateAsync(string connectionString)
    {
        var result = typeof(migrator::Program).Assembly.EntryPoint!.Invoke(null, [new[] { $"--ConnectionStrings:SbPayments={connectionString}" }]);
        return result is Task<int> task ? await task : (int)result!;
    }

    /// <summary>The simulator's server API, with its key.</summary>
    public HttpClient SimulatorClient()
    {
        var client = _simulator.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "sim-key");
        return client;
    }

    public async Task SetFaultsAsync(SimFaults faults) =>
        (await SimulatorClient().PutAsJsonAsync("/__faults", faults)).EnsureSuccessStatusCode();

    /// <summary>Plays the customer at the simulator's checkout.</summary>
    public async Task CheckoutAsync(Deposit deposit, bool pay = true) =>
        (await SimulatorClient().PostAsync(new Uri($"/v1/deposits/{deposit.Reference}/complete?outcome={(pay ? "succeeded" : "failed")}", UriKind.Relative), null)).EnsureSuccessStatusCode();

    /// <summary>Waits for the background webhook sender to have delivered <paramref name="count"/> webhooks.</summary>
    public async Task DeliveredAsync(int count)
    {
        for (var i = 0; i < 200 && Delivered < count; i++)
        {
            await Task.Delay(25);
        }

        Delivered.ShouldBeGreaterThanOrEqualTo(count, "webhooks delivered");
        await Task.Delay(50);
    }

    public int Delivered
    {
        get
        {
            lock (Deliveries)
            {
                return Deliveries.Count;
            }
        }
    }

    public async Task<int> OutboxCountAsync(string topicBase)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outbox.Messages WHERE Topic = @Topic", new { Topic = TopicName.For(topicBase, "test").Value });
    }

    public ValueTask DisposeAsync() => _simulator.DisposeAsync();

    private sealed class DeliverInProcess(PaymentsRig rig) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            HttpStatusCode status;
            try
            {
                status = await rig.Webhooks.ReceiveAsync("simulator", body, name => request.Headers.TryGetValues(name, out var values) ? values.First() : null, cancellationToken) switch
                {
                    WebhookOutcome.Accepted => HttpStatusCode.OK,
                    _ => HttpStatusCode.Unauthorized,
                };
            }
            catch (WalletUnavailableException)
            {
                status = HttpStatusCode.ServiceUnavailable;
            }

            lock (rig.Deliveries)
            {
                rig.Deliveries.Add(status);
            }

            return new HttpResponseMessage(status);
        }
    }
}
