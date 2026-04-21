using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenFeatureStalenessActiveUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLFeatureStalenessLog_MLModelId_FeatureName",
                table: "MLFeatureStalenessLog");

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureStalenessLog_MLModelId_FeatureName",
                table: "MLFeatureStalenessLog",
                columns: new[] { "MLModelId", "FeatureName" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLFeatureStalenessLog_MLModelId_FeatureName",
                table: "MLFeatureStalenessLog");

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureStalenessLog_MLModelId_FeatureName",
                table: "MLFeatureStalenessLog",
                columns: new[] { "MLModelId", "FeatureName" });
        }
    }
}
