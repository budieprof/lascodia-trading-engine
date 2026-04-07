using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StrategyGenerationAPlusPersistenceHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "WalkForwardRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ValidationQueueKey",
                table: "WalkForwardRun",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationCandidateId",
                table: "Strategy",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationCycleId",
                table: "Strategy",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PrunedAtUtc",
                table: "Strategy",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValidationPriority",
                table: "Strategy",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ValidationQueueKey",
                table: "BacktestRun",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StrategyGenerationCheckpoint",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CycleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CycleDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    UsedRestartSafeFallback = table.Column<bool>(type: "boolean", nullable: false),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationCheckpoint", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyGenerationFailure",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CandidateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CycleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CandidateHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StrategyType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    FailureStage = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DetailsJson = table.Column<string>(type: "text", nullable: true),
                    IsReported = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationFailure", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyGenerationPendingArtifact",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CycleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CandidatePayloadJson = table.Column<string>(type: "text", nullable: false),
                    NeedsCreationAudit = table.Column<bool>(type: "boolean", nullable: false),
                    NeedsCreatedEvent = table.Column<bool>(type: "boolean", nullable: false),
                    NeedsAutoPromoteEvent = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationPendingArtifact", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_ValidationQueueKey",
                table: "WalkForwardRun",
                column: "ValidationQueueKey",
                unique: true,
                filter: "\"ValidationQueueKey\" IS NOT NULL AND \"Status\" = 'Queued' AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Strategy_GenerationCandidateId",
                table: "Strategy",
                column: "GenerationCandidateId",
                unique: true,
                filter: "\"GenerationCandidateId\" IS NOT NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Strategy_IsDeleted_PrunedAtUtc",
                table: "Strategy",
                columns: new[] { "IsDeleted", "PrunedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_ValidationQueueKey",
                table: "BacktestRun",
                column: "ValidationQueueKey",
                unique: true,
                filter: "\"ValidationQueueKey\" IS NOT NULL AND \"Status\" = 'Queued' AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationCheckpoint_WorkerName",
                table: "StrategyGenerationCheckpoint",
                column: "WorkerName",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationFailure_CandidateId_FailureStage_Resolved~",
                table: "StrategyGenerationFailure",
                columns: new[] { "CandidateId", "FailureStage", "ResolvedAtUtc", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationFailure_IsReported_ResolvedAtUtc_IsDeleted",
                table: "StrategyGenerationFailure",
                columns: new[] { "IsReported", "ResolvedAtUtc", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationPendingArtifact_CandidateId",
                table: "StrategyGenerationPendingArtifact",
                column: "CandidateId",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationPendingArtifact_StrategyId",
                table: "StrategyGenerationPendingArtifact",
                column: "StrategyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StrategyGenerationCheckpoint");

            migrationBuilder.DropTable(
                name: "StrategyGenerationFailure");

            migrationBuilder.DropTable(
                name: "StrategyGenerationPendingArtifact");

            migrationBuilder.DropIndex(
                name: "IX_WalkForwardRun_ValidationQueueKey",
                table: "WalkForwardRun");

            migrationBuilder.DropIndex(
                name: "IX_Strategy_GenerationCandidateId",
                table: "Strategy");

            migrationBuilder.DropIndex(
                name: "IX_Strategy_IsDeleted_PrunedAtUtc",
                table: "Strategy");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_ValidationQueueKey",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "ValidationQueueKey",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "GenerationCandidateId",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "GenerationCycleId",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "PrunedAtUtc",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "ValidationPriority",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "ValidationQueueKey",
                table: "BacktestRun");
        }
    }
}
