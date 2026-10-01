extern alias migrator;

using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using SwiftBets.BuildingBlocks.Outbox;
using SwiftBets.BuildingBlocks.Persistence;
using SwiftBets.BuildingBlocks.Testing;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]

namespace SwiftBets.Payments.Api.Tests;

/// <summary>The payments host on a freshly migrated database, accepting tokens from <see cref="TestJwt"/>. Nothing outside it is reachable.</summary>
public sealed class PaymentsHost : WebApplicationFactory<Program>
{
    public const string WebhookSecret = "whsec_api_tests";
    private readonly string _connectionString;

    private PaymentsHost(string connectionString) => _connectionString = connectionString;

    public static async Task<PaymentsHost> StartAsync(SqlServerFixture sql)
    {
        var connectionString = await sql.CreateDatabaseAsync("payh_" + Guid.NewGuid().ToString("N")[..10]);
        MigrationRunner.RunSqlServer(connectionString, false, OutboxRegistration.Migrations, new MigrationSource(typeof(migrator::Program).Assembly, 1)).Successful.ShouldBeTrue();
        return new PaymentsHost(connectionString);
    }

    public HttpClient ClientFor(string subject, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.Issue(subject, roles));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:SbPayments", _connectionString);
        builder.UseSetting("Jwt:Authority", TestJwt.Issuer);
        builder.UseSetting("Jwt:Audience", TestJwt.Audience);
        builder.UseSetting("Jwt:RequireHttpsMetadata", "false");
        builder.UseSetting("Kafka:BootstrapServers", "127.0.0.1:9");
        builder.UseSetting("Kafka:Environment", "test");
        builder.UseSetting("Kafka:ClientId", "payments-tests");
        builder.UseSetting("Outbox:RunRelay", "false");
        builder.UseSetting("Payments:RunWorkers", "false");
        builder.UseSetting("Payments:Simulator:BaseUrl", "http://127.0.0.1:9");
        builder.UseSetting("Payments:Simulator:WebhookSecrets:0", WebhookSecret);
        builder.UseSetting("Wallet:GrpcAddress", "http://127.0.0.1:9");
        builder.UseSetting("ServiceIdentity:TokenEndpoint", "http://127.0.0.1:9/auth/token");
        builder.UseSetting("ServiceIdentity:ClientId", "payments");
        builder.UseSetting("ServiceIdentity:ClientSecret", "secret");
        builder.ConfigureTestServices(services => services.UseTestJwt());
    }
}
