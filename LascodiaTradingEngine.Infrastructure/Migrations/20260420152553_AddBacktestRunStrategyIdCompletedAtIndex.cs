using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBacktestRunStrategyIdCompletedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_StrategyId_CompletedAtDesc",
                table: "BacktestRun",
                columns: new[] { "StrategyId", "CompletedAt" },
                descending: new[] { false, true },
                filter: "\"Status\" = 'Completed' AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_StrategyId_CompletedAtDesc",
                table: "BacktestRun");
        }
    }
}
