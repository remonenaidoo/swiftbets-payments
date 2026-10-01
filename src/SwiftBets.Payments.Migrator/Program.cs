using Microsoft.Extensions.Configuration;
using SwiftBets.BuildingBlocks.Outbox;
using SwiftBets.BuildingBlocks.Persistence;

var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
var connectionString = configuration["ConnectionStrings:SbPayments"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync("ConnectionStrings:SbPayments is required.");
    return 2;
}

var source = new MigrationSource(typeof(Program).Assembly, 1);
if (configuration["Migrator:RollbackTo"] is { Length: > 0 } target)
{
    var rollback = MigrationRollback.SqlServer(connectionString, source, int.Parse(target, System.Globalization.CultureInfo.InvariantCulture));
    await Console.Out.WriteLineAsync(rollback.Successful ? $"rolled back {rollback.RolledBack.Count} migrations" : rollback.Error);
    return rollback.Successful ? 0 : 1;
}

var result = MigrationRunner.RunSqlServer(connectionString, configuration.GetValue("Migrator:EnsureDatabase", false), OutboxRegistration.Migrations, source);
if (!result.Successful)
{
    await Console.Error.WriteLineAsync(result.Error.ToString());
    return 1;
}

if (configuration["Migrator:AppLogin"] is { Length: > 0 } appLogin)
{
    await MigrationRunner.GrantSqlServerAppLoginAsync(connectionString, appLogin, CancellationToken.None);
}

return 0;

public partial class Program;
