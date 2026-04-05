using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizationWorkerRobustnessHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalkForwardRun_SourceOptimizationRunId",
                table: "WalkForwardRun");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_SourceOptimizationRunId",
                table: "BacktestRun");

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidationFollowUpsCreatedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_SourceOptimizationRunId",
                table: "WalkForwardRun",
                column: "SourceOptimizationRunId",
                unique: true,
                filter: "\"SourceOptimizationRunId\" IS NOT NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_ValidationFollowUpsCreatedAt",
                table: "OptimizationRun",
                column: "ValidationFollowUpsCreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_SourceOptimizationRunId",
                table: "BacktestRun",
                column: "SourceOptimizationRunId",
                unique: true,
                filter: "\"SourceOptimizationRunId\" IS NOT NULL AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalkForwardRun_SourceOptimizationRunId",
                table: "WalkForwardRun");

            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_ValidationFollowUpsCreatedAt",
                table: "OptimizationRun");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_SourceOptimizationRunId",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "ValidationFollowUpsCreatedAt",
                table: "OptimizationRun");

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_SourceOptimizationRunId",
                table: "WalkForwardRun",
                column: "SourceOptimizationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_SourceOptimizationRunId",
                table: "BacktestRun",
                column: "SourceOptimizationRunId");
        }
    }
}
