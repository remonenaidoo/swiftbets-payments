extern alias migrator;

using Dapper;
using Microsoft.Data.SqlClient;
using SwiftBets.BuildingBlocks.Testing;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Infrastructure.Tests;

public sealed class MigrationTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Migrations_are_idempotent_and_reconciliation_rolls_back_and_reapplies()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        (await PaymentsRig.MigrateAsync(rig.ConnectionString)).ShouldBe(0);
        await using var connection = new SqlConnection(rig.ConnectionString);

        await connection.ExecuteAsync(Rollback("0003_reconciliation"));
        (await Tables(connection, "Reconciliation%")).ShouldBe(0);
        await connection.ExecuteAsync("DELETE FROM dbo.SchemaVersions WHERE ScriptName LIKE '%0003_reconciliation.sql'");

        (await PaymentsRig.MigrateAsync(rig.ConnectionString)).ShouldBe(0);
        (await Tables(connection, "Reconciliation%")).ShouldBe(2);
    }

    [Fact]
    public async Task Payment_tables_refuse_to_roll_back_once_money_has_moved()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        await rig.Store.InsertDepositAsync(Deposit.Start(Guid.NewGuid(), Guid.NewGuid(), 1_000, "ZAR", "simulator", rig.Time.GetUtcNow()), TestContext.Current.CancellationToken);
        await using var connection = new SqlConnection(rig.ConnectionString);

        await Should.ThrowAsync<SqlException>(() => connection.ExecuteAsync(Rollback("0002_deposits_and_withdrawals")));
        (await Tables(connection, "Deposits")).ShouldBe(1);
    }

    private static Task<int> Tables(SqlConnection connection, string like) =>
        connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'payments' AND t.name LIKE @Like", new { Like = like });

    private static string Rollback(string migration)
    {
        using var stream = typeof(migrator::Program).Assembly.GetManifestResourceStream($"SwiftBets.Payments.Migrator.Rollbacks.{migration}.sql")!;
        return new StreamReader(stream).ReadToEnd();
    }
}
