using Microsoft.EntityFrameworkCore.Migrations;

namespace LascodiaTradingEngine.Infrastructure.Migrations;

public partial class SignalAccountAttemptUniqueIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Drop the existing non-unique index
        migrationBuilder.DropIndex(
            name: "IX_SignalAccountAttempts_TradeSignalId_TradingAccountId",
            table: "SignalAccountAttempts");

        // Create filtered unique index (only non-deleted rows)
        migrationBuilder.CreateIndex(
            name: "IX_SignalAccountAttempts_TradeSignalId_TradingAccountId",
            table: "SignalAccountAttempts",
            columns: new[] { "TradeSignalId", "TradingAccountId" },
            unique: true,
            filter: "\"IsDeleted\" = false");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SignalAccountAttempts_TradeSignalId_TradingAccountId",
            table: "SignalAccountAttempts");

        migrationBuilder.CreateIndex(
            name: "IX_SignalAccountAttempts_TradeSignalId_TradingAccountId",
            table: "SignalAccountAttempts",
            columns: new[] { "TradeSignalId", "TradingAccountId" });
    }
}
