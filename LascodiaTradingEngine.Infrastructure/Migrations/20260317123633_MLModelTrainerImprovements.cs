using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLModelTrainerImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SharpeRatio",
                table: "MLTrainingRun",
                type: "numeric(10,4)",
                precision: 10,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkerInstanceId",
                table: "MLTrainingRun",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PlattA",
                table: "MLModel",
                type: "numeric(10,6)",
                precision: 10,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PlattB",
                table: "MLModel",
                type: "numeric(10,6)",
                precision: 10,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SharpeRatio",
                table: "MLModel",
                type: "numeric(10,4)",
                precision: 10,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SharpeRatio",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "WorkerInstanceId",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "PlattA",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "PlattB",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "SharpeRatio",
                table: "MLModel");
        }
    }
}
