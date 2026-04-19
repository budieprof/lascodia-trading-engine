using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStrategyRuntimeState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CircuitOpenedAt",
                table: "Strategy",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveEvaluationFailures",
                table: "Strategy",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSignalAt",
                table: "Strategy",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CircuitOpenedAt",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "ConsecutiveEvaluationFailures",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "LastSignalAt",
                table: "Strategy");
        }
    }
}
