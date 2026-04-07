using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizationWorkerAPlusHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RolloutEvaluationFailureCount",
                table: "Strategy",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "RolloutLastAverageHealthScore",
                table: "Strategy",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RolloutLastEvaluatedAt",
                table: "Strategy",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RolloutLastFailureMessage",
                table: "Strategy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RolloutLastOutcome",
                table: "Strategy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompletionPublicationAttempts",
                table: "OptimizationRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletionPublicationCompletedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletionPublicationErrorMessage",
                table: "OptimizationRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletionPublicationLastAttemptAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletionPublicationPayloadJson",
                table: "OptimizationRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompletionPublicationStatus",
                table: "OptimizationRun",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionStartedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FollowUpLastCheckedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FollowUpRepairAttempts",
                table: "OptimizationRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextFollowUpCheckAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QueuedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RolloutEvaluationFailureCount",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "RolloutLastAverageHealthScore",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "RolloutLastEvaluatedAt",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "RolloutLastFailureMessage",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "RolloutLastOutcome",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "CompletionPublicationAttempts",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "CompletionPublicationCompletedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "CompletionPublicationErrorMessage",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "CompletionPublicationLastAttemptAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "CompletionPublicationPayloadJson",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "CompletionPublicationStatus",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "ExecutionStartedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "FollowUpLastCheckedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "FollowUpRepairAttempts",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "NextFollowUpCheckAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "QueuedAt",
                table: "OptimizationRun");
        }
    }
}
