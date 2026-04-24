using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using LascodiaTradingEngine.Infrastructure.Migrations;

namespace LascodiaTradingEngine.UnitTest.Infrastructure.Migrations;

public sealed class MigrationContractTests
{
    [Fact]
    public void EnforceSingleActiveTradingAccount_RepairsDuplicateActiveRowsBeforeCreatingUniqueIndex()
    {
        var migration = new EnforceSingleActiveTradingAccount();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");

        typeof(EnforceSingleActiveTradingAccount)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var sql = Assert.Single(builder.Operations.OfType<SqlOperation>()).Sql;

        Assert.Contains("ROW_NUMBER()", sql);
        Assert.Contains("SET \"IsActive\" = false", sql);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TradingAccount_IsActive_SingleTrue\"", sql);
    }
}
