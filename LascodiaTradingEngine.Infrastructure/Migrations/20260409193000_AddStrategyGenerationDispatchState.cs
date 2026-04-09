using System;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    [DbContext(typeof(WriteApplicationDbContext))]
    [Migration("20260409193000_AddStrategyGenerationDispatchState")]
    public partial class AddStrategyGenerationDispatchState : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SummaryEventDispatchedAtUtc",
                table: "StrategyGenerationCycleRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SummaryEventDispatchAttempts",
                table: "StrategyGenerationCycleRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SummaryEventFailedAtUtc",
                table: "StrategyGenerationCycleRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SummaryEventFailureMessage",
                table: "StrategyGenerationCycleRun",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SummaryEventId",
                table: "StrategyGenerationCycleRun",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SummaryEventPayloadJson",
                table: "StrategyGenerationCycleRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoPromotedEventDispatchedAtUtc",
                table: "StrategyGenerationPendingArtifact",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AutoPromotedEventId",
                table: "StrategyGenerationPendingArtifact",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CandidateCreatedEventDispatchedAtUtc",
                table: "StrategyGenerationPendingArtifact",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CandidateCreatedEventId",
                table: "StrategyGenerationPendingArtifact",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreationAuditLoggedAtUtc",
                table: "StrategyGenerationPendingArtifact",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuarantinedAtUtc",
                table: "StrategyGenerationPendingArtifact",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TerminalFailureReason",
                table: "StrategyGenerationPendingArtifact",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SummaryEventDispatchedAtUtc",
                table: "StrategyGenerationCycleRun");

            migrationBuilder.DropColumn(
                name: "SummaryEventDispatchAttempts",
                table: "StrategyGenerationCycleRun");

            migrationBuilder.DropColumn(
                name: "SummaryEventFailedAtUtc",
                table: "StrategyGenerationCycleRun");

            migrationBuilder.DropColumn(
                name: "SummaryEventFailureMessage",
                table: "StrategyGenerationCycleRun");

            migrationBuilder.DropColumn(
                name: "SummaryEventId",
                table: "StrategyGenerationCycleRun");

            migrationBuilder.DropColumn(
                name: "SummaryEventPayloadJson",
                table: "StrategyGenerationCycleRun");

            migrationBuilder.DropColumn(
                name: "AutoPromotedEventDispatchedAtUtc",
                table: "StrategyGenerationPendingArtifact");

            migrationBuilder.DropColumn(
                name: "AutoPromotedEventId",
                table: "StrategyGenerationPendingArtifact");

            migrationBuilder.DropColumn(
                name: "CandidateCreatedEventDispatchedAtUtc",
                table: "StrategyGenerationPendingArtifact");

            migrationBuilder.DropColumn(
                name: "CandidateCreatedEventId",
                table: "StrategyGenerationPendingArtifact");

            migrationBuilder.DropColumn(
                name: "CreationAuditLoggedAtUtc",
                table: "StrategyGenerationPendingArtifact");

            migrationBuilder.DropColumn(
                name: "QuarantinedAtUtc",
                table: "StrategyGenerationPendingArtifact");

            migrationBuilder.DropColumn(
                name: "TerminalFailureReason",
                table: "StrategyGenerationPendingArtifact");
        }
    }
}
