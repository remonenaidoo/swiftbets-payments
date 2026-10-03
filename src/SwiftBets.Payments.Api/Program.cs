using SwiftBets.BuildingBlocks.Observability;
using SwiftBets.BuildingBlocks.Web;
using SwiftBets.Payments.Api.Endpoints;
using SwiftBets.Payments.Application;
using SwiftBets.Payments.Infrastructure;

if (HealthProbe.TryRun(args) is { } probeExitCode)
{
    return probeExitCode;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddSwiftBetsObservability("swiftbets-payments");
builder.Services.AddSwiftBetsWeb();
builder.Services.AddSwiftBetsJwtBearer(builder.Configuration);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(PaymentPermissions.Read, p => p.RequireClaim("perm", PaymentPermissions.Read))
    .AddPolicy(PaymentPermissions.Approve, p => p.RequireClaim("perm", PaymentPermissions.Approve))
    .AddPolicy(PaymentPermissions.SweepNow, p => p.RequireAssertion(c => c.User.HasClaim("perm", PaymentPermissions.Approve) || c.User.IsInRole("Service")));
builder.Services.AddPaymentsApplication();
builder.Services.AddPaymentsInfrastructure(builder.Configuration);

var app = builder.Build();
app.UseSwiftBetsObservability();
app.UseSwiftBetsWeb();
app.UseAuthentication();
app.UseAuthorization();
app.MapSwiftBetsOperationalEndpoints();
app.MapCustomerPayments();
app.MapWebhooks();
app.MapAdminPayments();

await app.RunAsync();
return 0;

public partial class Program;
