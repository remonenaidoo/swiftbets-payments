using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Application;

public static class ApplicationRegistration
{
    public static IServiceCollection AddPaymentsApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(PaymentRules.Default);
        services.AddSingleton<DepositHandler>();
        services.AddSingleton<WithdrawalHandler>();
        services.AddSingleton<WebhookHandler>();
        services.AddSingleton<OpenPaymentsHandler>();
        services.AddSingleton<ReconciliationHandler>();
        return services;
    }
}
