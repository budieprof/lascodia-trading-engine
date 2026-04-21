using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenKellyFractionLogReliability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReliable",
                table: "MLKellyFractionLogs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "LossCount",
                table: "MLKellyFractionLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PnlBasedSamples",
                table: "MLKellyFractionLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "MLKellyFractionLogs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Computed");

            migrationBuilder.AddColumn<int>(
                name: "TotalResolvedSamples",
                table: "MLKellyFractionLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UsableSamples",
                table: "MLKellyFractionLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WinCount",
                table: "MLKellyFractionLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_TradeSignal_StrategyId_GeneratedAt",
                table: "TradeSignal",
                columns: new[] { "StrategyId", "GeneratedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLKellyFractionLogs_MLModelId_IsReliable_ComputedAt",
                table: "MLKellyFractionLogs",
                columns: new[] { "MLModelId", "IsReliable", "ComputedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradeSignal_StrategyId_GeneratedAt",
                table: "TradeSignal");

            migrationBuilder.DropIndex(
                name: "IX_MLKellyFractionLogs_MLModelId_IsReliable_ComputedAt",
                table: "MLKellyFractionLogs");

            migrationBuilder.DropColumn(
                name: "IsReliable",
                table: "MLKellyFractionLogs");

            migrationBuilder.DropColumn(
                name: "LossCount",
                table: "MLKellyFractionLogs");

            migrationBuilder.DropColumn(
                name: "PnlBasedSamples",
                table: "MLKellyFractionLogs");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "MLKellyFractionLogs");

            migrationBuilder.DropColumn(
                name: "TotalResolvedSamples",
                table: "MLKellyFractionLogs");

            migrationBuilder.DropColumn(
                name: "UsableSamples",
                table: "MLKellyFractionLogs");

            migrationBuilder.DropColumn(
                name: "WinCount",
                table: "MLKellyFractionLogs");
        }
    }
}
