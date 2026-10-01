using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using SwiftBets.BuildingBlocks.Core;
using SwiftBets.BuildingBlocks.Messaging;
using SwiftBets.BuildingBlocks.Outbox;
using SwiftBets.BuildingBlocks.Persistence;
using SwiftBets.BuildingBlocks.Resilience;
using SwiftBets.BuildingBlocks.Web;
using SwiftBets.Contracts.Compliance;
using SwiftBets.Contracts.Messaging;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Infrastructure.Compliance;
using SwiftBets.Payments.Infrastructure.Persistence;
using SwiftBets.Payments.Infrastructure.Providers;
using SwiftBets.Payments.Infrastructure.Wallet;
using SwiftBets.Payments.Infrastructure.Workers;
using WalletGrpc = SwiftBets.Contracts.Grpc.Wallet.V1.Wallet;

namespace SwiftBets.Payments.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddPaymentsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSqlServerPersistence(Required(configuration, "ConnectionStrings:SbPayments"));
        services.AddValidatedOptions<PaymentsOptions>(configuration, PaymentsOptions.SectionName);
        services.AddValidatedOptions<WalletOptions>(configuration, WalletOptions.SectionName);
        services.Configure<SimulatorOptions>(configuration.GetSection(SimulatorOptions.SectionName));
        services.Configure<PaystackOptions>(configuration.GetSection(PaystackOptions.SectionName));
        services.AddSingleton<IPaymentStore, SqlPaymentStore>();
        services.AddSingleton<PaymentsMetrics>();

        // Events leave through the outbox; the relay runs in every replica.
        services.AddKafkaMessaging(configuration);
        services.AddSqlServerOutbox(configuration, runRelay: configuration.GetValue("Outbox:RunRelay", true));

        // KYC status from compliance's compacted snapshot; payments reports unready until it is caught up.
        services.AddCompactedState<RestrictionsChangedV1>(Topics.RestrictionsChanged);
        services.AddSingleton<ICustomerStatus, CompactedCustomerStatus>();

        services.AddHttpClient<SimulatorProvider>((sp, http) => http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SimulatorOptions>>().Value.BaseUrl.TrimEnd('/') + "/"))
            .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
        services.AddHttpClient<PaystackProvider>((sp, http) => http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PaystackOptions>>().Value.BaseUrl.TrimEnd('/') + "/"))
            .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
        services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<SimulatorProvider>());
        if (!string.IsNullOrEmpty(configuration[$"{PaystackOptions.SectionName}:SecretKey"]))
        {
            services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<PaystackProvider>());
        }

        services.AddSingleton<IPaymentProviders, PaymentProviders>();

        services.AddClientCredentials(configuration);
        services.AddSingleton<IWalletClient, GrpcWalletClient>();
        services.AddGrpcClient<WalletGrpc.WalletClient>((sp, grpc) => grpc.Address = new Uri(sp.GetRequiredService<IOptions<WalletOptions>>().Value.GrpcAddress))
            .ConfigureChannel(channel =>
            {
                channel.ServiceConfig = GrpcResilience.KeyedServiceConfig;
                channel.UnsafeUseInsecureChannelCallCredentials = true;
            })
            .AddCallCredentials(async (context, metadata, sp) =>
                metadata.Add("Authorization", $"Bearer {await sp.GetRequiredService<ClientCredentialsTokenProvider>().GetTokenAsync(context.CancellationToken)}"))
            .AddKeyedGrpcResilience();

        if (configuration.GetValue("Payments:RunWorkers", true))
        {
            services.AddHostedService<OpenPaymentsWorker>();
            services.AddHostedService<DailyReconciliationWorker>();
        }

        return services;
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Configuration '{key}' is required.");
}
