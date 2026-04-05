using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOptimizationRunDeferredUntilUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RollbackParametersJson",
                table: "Strategy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RolloutOptimizationRunId",
                table: "Strategy",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RolloutPct",
                table: "Strategy",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RolloutStartedAt",
                table: "Strategy",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BestMaxDrawdownPct",
                table: "OptimizationRun",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BestSharpeRatio",
                table: "OptimizationRun",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BestWinRate",
                table: "OptimizationRun",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeferredUntilUtc",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureCategory",
                table: "OptimizationRun",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_DeferredUntilUtc",
                table: "OptimizationRun",
                columns: new[] { "Status", "DeferredUntilUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_Status_DeferredUntilUtc",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "RollbackParametersJson",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "RolloutOptimizationRunId",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "RolloutPct",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "RolloutStartedAt",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "BestMaxDrawdownPct",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "BestSharpeRatio",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "BestWinRate",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "DeferredUntilUtc",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "FailureCategory",
                table: "OptimizationRun");
        }
    }
}
