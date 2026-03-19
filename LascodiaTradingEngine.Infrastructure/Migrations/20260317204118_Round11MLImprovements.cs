using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Round11MLImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HorizonCorrect12",
                table: "MLModelPredictionLog",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HorizonCorrect3",
                table: "MLModelPredictionLog",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HorizonCorrect6",
                table: "MLModelPredictionLog",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSuppressed",
                table: "MLModel",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HorizonCorrect12",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "HorizonCorrect3",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "HorizonCorrect6",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "IsSuppressed",
                table: "MLModel");
        }
    }
}
