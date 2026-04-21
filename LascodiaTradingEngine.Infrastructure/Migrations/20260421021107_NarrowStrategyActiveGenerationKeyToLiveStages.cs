using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NarrowStrategyActiveGenerationKeyToLiveStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Strategy_ActiveGenerationKey",
                table: "Strategy");

            migrationBuilder.CreateIndex(
                name: "IX_Strategy_ActiveGenerationKey",
                table: "Strategy",
                columns: new[] { "StrategyType", "Symbol", "Timeframe" },
                unique: true,
                filter: "\"IsDeleted\" = false AND \"LifecycleStage\" IN ('Approved', 'Active')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Strategy_ActiveGenerationKey",
                table: "Strategy");

            migrationBuilder.CreateIndex(
                name: "IX_Strategy_ActiveGenerationKey",
                table: "Strategy",
                columns: new[] { "StrategyType", "Symbol", "Timeframe" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }
    }
}
