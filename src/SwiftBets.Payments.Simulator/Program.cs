using SwiftBets.BuildingBlocks.Observability;
using SwiftBets.Payments.Simulator;

if (HealthProbe.TryRun(args) is { } probeExitCode)
{
    return probeExitCode;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddSwiftBetsObservability("swiftbets-payments-simulator");
builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection(SimulatorOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SimulatorState>();
builder.Services.AddHttpClient("webhooks");
builder.Services.AddSingleton<WebhookSender>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookSender>());

var app = builder.Build();
app.UseSwiftBetsObservability();
app.MapSwiftBetsOperationalEndpoints();
app.MapSimulator();

await app.RunAsync();
return 0;

