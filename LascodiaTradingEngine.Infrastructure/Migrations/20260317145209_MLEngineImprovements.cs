using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLEngineImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HyperparamConfigJson",
                table: "MLTrainingRun",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EnsembleDisagreement",
                table: "MLModelPredictionLog",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionSource",
                table: "MLModelPredictionLog",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HyperparamConfigJson",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "EnsembleDisagreement",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "ResolutionSource",
                table: "MLModelPredictionLog");
        }
    }
}
