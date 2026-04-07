using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StrategyGenerationAPlusArchitectureRefactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StrategyGenerationCycleRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CycleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<double>(type: "double precision", nullable: true),
                    CandidatesCreated = table.Column<int>(type: "integer", nullable: false),
                    ReserveCandidatesCreated = table.Column<int>(type: "integer", nullable: false),
                    CandidatesScreened = table.Column<int>(type: "integer", nullable: false),
                    SymbolsProcessed = table.Column<int>(type: "integer", nullable: false),
                    SymbolsSkipped = table.Column<int>(type: "integer", nullable: false),
                    StrategiesPruned = table.Column<int>(type: "integer", nullable: false),
                    PortfolioFilterRemoved = table.Column<int>(type: "integer", nullable: false),
                    FailureStage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationCycleRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyGenerationScheduleState",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastRunDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CircuitBreakerUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    RetriesThisWindow = table.Column<int>(type: "integer", nullable: false),
                    RetryWindowDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationScheduleState", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationCycleRun_CycleId",
                table: "StrategyGenerationCycleRun",
                column: "CycleId",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationCycleRun_WorkerName_StartedAtUtc_IsDeleted",
                table: "StrategyGenerationCycleRun",
                columns: new[] { "WorkerName", "StartedAtUtc", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationScheduleState_WorkerName",
                table: "StrategyGenerationScheduleState",
                column: "WorkerName",
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StrategyGenerationCycleRun");

            migrationBuilder.DropTable(
                name: "StrategyGenerationScheduleState");
        }
    }
}
