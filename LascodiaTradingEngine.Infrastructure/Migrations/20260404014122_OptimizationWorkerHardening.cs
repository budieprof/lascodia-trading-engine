using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizationWorkerHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SourceOptimizationRunId",
                table: "WalkForwardRun",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalReportJson",
                table: "OptimizationRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CheckpointVersion",
                table: "OptimizationRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ConfigSnapshotJson",
                table: "OptimizationRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeterministicSeed",
                table: "OptimizationRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionLeaseExpiresAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeartbeatAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunMetadataJson",
                table: "OptimizationRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TradingHoursJson",
                table: "CurrencyPair",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceOptimizationRunId",
                table: "BacktestRun",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_SourceOptimizationRunId",
                table: "WalkForwardRun",
                column: "SourceOptimizationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_ExecutionLeaseExpiresAt",
                table: "OptimizationRun",
                columns: new[] { "Status", "ExecutionLeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_SourceOptimizationRunId",
                table: "BacktestRun",
                column: "SourceOptimizationRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalkForwardRun_SourceOptimizationRunId",
                table: "WalkForwardRun");

            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_Status_ExecutionLeaseExpiresAt",
                table: "OptimizationRun");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_SourceOptimizationRunId",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "SourceOptimizationRunId",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "ApprovalReportJson",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "CheckpointVersion",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "ConfigSnapshotJson",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "DeterministicSeed",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "ExecutionLeaseExpiresAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "RunMetadataJson",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "TradingHoursJson",
                table: "CurrencyPair");

            migrationBuilder.DropColumn(
                name: "SourceOptimizationRunId",
                table: "BacktestRun");
        }
    }
}
