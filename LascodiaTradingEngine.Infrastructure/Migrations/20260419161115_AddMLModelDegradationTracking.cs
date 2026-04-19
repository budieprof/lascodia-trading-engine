using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMLModelDegradationTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveRetrainFailures",
                table: "MLModel",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DegradationRetiredAt",
                table: "MLModel",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveRetrainFailures",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "DegradationRetiredAt",
                table: "MLModel");
        }
    }
}
