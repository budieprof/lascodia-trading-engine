using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountPerformanceAttributionGranularity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountPerformanceAttribution_TradingAccountId_AttributionD~",
                table: "AccountPerformanceAttribution");

            migrationBuilder.AddColumn<int>(
                name: "Granularity",
                table: "AccountPerformanceAttribution",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_AccountPerformanceAttribution_TradingAccountId_AttributionD~",
                table: "AccountPerformanceAttribution",
                columns: new[] { "TradingAccountId", "AttributionDate", "Granularity" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountPerformanceAttribution_TradingAccountId_AttributionD~",
                table: "AccountPerformanceAttribution");

            migrationBuilder.DropColumn(
                name: "Granularity",
                table: "AccountPerformanceAttribution");

            migrationBuilder.CreateIndex(
                name: "IX_AccountPerformanceAttribution_TradingAccountId_AttributionD~",
                table: "AccountPerformanceAttribution",
                columns: new[] { "TradingAccountId", "AttributionDate" },
                unique: true);
        }
    }
}
