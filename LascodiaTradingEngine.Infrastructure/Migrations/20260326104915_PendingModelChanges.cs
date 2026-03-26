using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PendingModelChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "TradingAccount",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<decimal>(
                name: "PartialClosePercent",
                table: "TradeSignal",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PartialTakeProfit",
                table: "TradeSignal",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastProcessedDeltaSequence",
                table: "EAInstance",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TradingAccountId1",
                table: "EAInstance",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EAInstance_InstanceId",
                table: "EAInstance",
                column: "InstanceId",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_EAInstance_TradingAccountId1",
                table: "EAInstance",
                column: "TradingAccountId1");

            migrationBuilder.AddForeignKey(
                name: "FK_EAInstance_TradingAccount_TradingAccountId1",
                table: "EAInstance",
                column: "TradingAccountId1",
                principalTable: "TradingAccount",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EAInstance_TradingAccount_TradingAccountId1",
                table: "EAInstance");

            migrationBuilder.DropIndex(
                name: "IX_EAInstance_InstanceId",
                table: "EAInstance");

            migrationBuilder.DropIndex(
                name: "IX_EAInstance_TradingAccountId1",
                table: "EAInstance");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "PartialClosePercent",
                table: "TradeSignal");

            migrationBuilder.DropColumn(
                name: "PartialTakeProfit",
                table: "TradeSignal");

            migrationBuilder.DropColumn(
                name: "LastProcessedDeltaSequence",
                table: "EAInstance");

            migrationBuilder.DropColumn(
                name: "TradingAccountId1",
                table: "EAInstance");
        }
    }
}
