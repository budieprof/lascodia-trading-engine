using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOptimizationRunActivePerStrategyUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_StrategyId",
                table: "OptimizationRun");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_ActivePerStrategy",
                table: "OptimizationRun",
                column: "StrategyId",
                unique: true,
                filter: "\"Status\" IN ('Queued','Running') AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_ActivePerStrategy",
                table: "OptimizationRun");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_StrategyId",
                table: "OptimizationRun",
                column: "StrategyId");
        }
    }
}
