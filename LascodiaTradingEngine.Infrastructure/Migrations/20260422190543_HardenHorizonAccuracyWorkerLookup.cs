using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenHorizonAccuracyWorkerLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MLModelPredictionLog_HorizonAccuracyLookup",
                table: "MLModelPredictionLog",
                columns: new[] { "MLModelId", "ModelRole", "PredictedAt" },
                filter: "\"IsDeleted\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLModelPredictionLog_HorizonAccuracyLookup",
                table: "MLModelPredictionLog");
        }
    }
}
