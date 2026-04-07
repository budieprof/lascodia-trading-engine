using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizationRunExecutionStageTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionStage",
                table: "OptimizationRun",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionStageMessage",
                table: "OptimizationRun",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionStageUpdatedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastOperationalIssueAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastOperationalIssueCode",
                table: "OptimizationRun",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastOperationalIssueMessage",
                table: "OptimizationRun",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_ExecutionStageUpdatedAt",
                table: "OptimizationRun",
                columns: new[] { "Status", "ExecutionStageUpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_Status_ExecutionStageUpdatedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "ExecutionStage",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "ExecutionStageMessage",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "ExecutionStageUpdatedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "LastOperationalIssueAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "LastOperationalIssueCode",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "LastOperationalIssueMessage",
                table: "OptimizationRun");
        }
    }
}
