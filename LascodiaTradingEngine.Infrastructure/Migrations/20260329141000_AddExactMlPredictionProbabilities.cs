using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    [DbContext(typeof(WriteApplicationDbContext))]
    [Migration("20260329141000_AddExactMlPredictionProbabilities")]
    public partial class AddExactMlPredictionProbabilities : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CalibratedProbability",
                table: "MLModelPredictionLog",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DecisionThresholdUsed",
                table: "MLModelPredictionLog",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RawProbability",
                table: "MLModelPredictionLog",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalibratedProbability",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "DecisionThresholdUsed",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "RawProbability",
                table: "MLModelPredictionLog");
        }
    }
}
