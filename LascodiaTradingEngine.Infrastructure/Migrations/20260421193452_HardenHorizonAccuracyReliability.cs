using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenHorizonAccuracyReliability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLModelHorizonAccuracy_MLModelId_HorizonBars",
                table: "MLModelHorizonAccuracy");

            migrationBuilder.AddColumn<double>(
                name: "AccuracyLowerBound",
                table: "MLModelHorizonAccuracy",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "IsReliable",
                table: "MLModelHorizonAccuracy",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<double>(
                name: "PrimaryAccuracy",
                table: "MLModelHorizonAccuracy",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "PrimaryAccuracyGap",
                table: "MLModelHorizonAccuracy",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "PrimaryCorrectPredictions",
                table: "MLModelHorizonAccuracy",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PrimaryTotalPredictions",
                table: "MLModelHorizonAccuracy",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "MLModelHorizonAccuracy",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Computed");

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHorizonAccuracy_MLModelId_HorizonBars",
                table: "MLModelHorizonAccuracy",
                columns: new[] { "MLModelId", "HorizonBars" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHorizonAccuracy_MLModelId_IsReliable_ComputedAt",
                table: "MLModelHorizonAccuracy",
                columns: new[] { "MLModelId", "IsReliable", "ComputedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLModelHorizonAccuracy_MLModelId_HorizonBars",
                table: "MLModelHorizonAccuracy");

            migrationBuilder.DropIndex(
                name: "IX_MLModelHorizonAccuracy_MLModelId_IsReliable_ComputedAt",
                table: "MLModelHorizonAccuracy");

            migrationBuilder.DropColumn(
                name: "AccuracyLowerBound",
                table: "MLModelHorizonAccuracy");

            migrationBuilder.DropColumn(
                name: "IsReliable",
                table: "MLModelHorizonAccuracy");

            migrationBuilder.DropColumn(
                name: "PrimaryAccuracy",
                table: "MLModelHorizonAccuracy");

            migrationBuilder.DropColumn(
                name: "PrimaryAccuracyGap",
                table: "MLModelHorizonAccuracy");

            migrationBuilder.DropColumn(
                name: "PrimaryCorrectPredictions",
                table: "MLModelHorizonAccuracy");

            migrationBuilder.DropColumn(
                name: "PrimaryTotalPredictions",
                table: "MLModelHorizonAccuracy");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "MLModelHorizonAccuracy");

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHorizonAccuracy_MLModelId_HorizonBars",
                table: "MLModelHorizonAccuracy",
                columns: new[] { "MLModelId", "HorizonBars" },
                unique: true);
        }
    }
}
