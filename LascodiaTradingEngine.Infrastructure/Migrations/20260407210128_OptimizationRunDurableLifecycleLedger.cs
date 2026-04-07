using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizationRunDurableLifecycleLedger : Migration
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
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationCandidateId",
                table: "Strategy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationCycleId",
                table: "Strategy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PauseReason",
                table: "Strategy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PrunedAtUtc",
                table: "Strategy",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RolloutEvaluationFailureCount",
                table: "Strategy",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RolloutLastFailureMessage",
                table: "Strategy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValidationPriority",
                table: "Strategy",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Strategy",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalEvaluatedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
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

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletionPublicationPreparedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompletionPublicationStatus",
                table: "OptimizationRun",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionStage",
                table: "OptimizationRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionStageMessage",
                table: "OptimizationRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionStageUpdatedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
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

            migrationBuilder.AddColumn<string>(
                name: "FollowUpLastStatusCode",
                table: "OptimizationRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FollowUpLastStatusMessage",
                table: "OptimizationRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FollowUpRepairAttempts",
                table: "OptimizationRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "FollowUpStatusUpdatedAt",
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
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastOperationalIssueMessage",
                table: "OptimizationRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LifecycleReconciledAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

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

            migrationBuilder.AddColumn<DateTime>(
                name: "ResultsPersistedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "OptimizationRun",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<long>(
                name: "LastProcessedDealSnapshotSequence",
                table: "EAInstance",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastProcessedOrderSnapshotSequence",
                table: "EAInstance",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastProcessedPositionSnapshotSequence",
                table: "EAInstance",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValidationQueueKey",
                table: "BacktestRun",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertDispatchLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    DispatchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertDispatchLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertDispatchLog_Alert_AlertId",
                        column: x => x.AlertId,
                        principalTable: "Alert",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrokerAccountSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<string>(type: "text", nullable: false),
                    Balance = table.Column<decimal>(type: "numeric", nullable: false),
                    Equity = table.Column<decimal>(type: "numeric", nullable: false),
                    MarginUsed = table.Column<decimal>(type: "numeric", nullable: false),
                    FreeMargin = table.Column<decimal>(type: "numeric", nullable: false),
                    ReportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrokerAccountSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PositionLifecycleEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PositionId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    PreviousLots = table.Column<decimal>(type: "numeric", nullable: true),
                    NewLots = table.Column<decimal>(type: "numeric", nullable: true),
                    SwapAccumulated = table.Column<decimal>(type: "numeric", nullable: true),
                    CommissionAccumulated = table.Column<decimal>(type: "numeric", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionLifecycleEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PositionLifecycleEvent_Position_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Position",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "StrategyGenerationFeedbackState",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StateKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationFeedbackState", x => x.Id);
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

            migrationBuilder.CreateTable(
                name: "TradingSessionSchedules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SessionName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OpenTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    CloseTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    DayOfWeekStart = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeekEnd = table.Column<int>(type: "integer", nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingSessionSchedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EACommand_CreatedAt",
                table: "EACommand",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EACommand_TargetInstanceId_Acknowledged",
                table: "EACommand",
                columns: new[] { "TargetInstanceId", "Acknowledged" },
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_AlertDispatchLog_AlertId",
                table: "AlertDispatchLog",
                column: "AlertId");

            migrationBuilder.CreateIndex(
                name: "IX_PositionLifecycleEvent_PositionId",
                table: "PositionLifecycleEvent",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationCheckpoint_WorkerName",
                table: "StrategyGenerationCheckpoint",
                column: "WorkerName",
                unique: true,
                filter: "\"IsDeleted\" = false");

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
                name: "IX_StrategyGenerationFailure_CandidateId_FailureStage_Resolved~",
                table: "StrategyGenerationFailure",
                columns: new[] { "CandidateId", "FailureStage", "ResolvedAtUtc", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationFailure_IsReported_ResolvedAtUtc_IsDeleted",
                table: "StrategyGenerationFailure",
                columns: new[] { "IsReported", "ResolvedAtUtc", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationFeedbackState_StateKey_IsDeleted",
                table: "StrategyGenerationFeedbackState",
                columns: new[] { "StateKey", "IsDeleted" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationScheduleState_WorkerName",
                table: "StrategyGenerationScheduleState",
                column: "WorkerName",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_TradingSessionSchedule_Symbol_Session_Instance",
                table: "TradingSessionSchedules",
                columns: new[] { "Symbol", "SessionName", "InstanceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertDispatchLog");

            migrationBuilder.DropTable(
                name: "BrokerAccountSnapshot");

            migrationBuilder.DropTable(
                name: "PositionLifecycleEvent");

            migrationBuilder.DropTable(
                name: "StrategyGenerationCheckpoint");

            migrationBuilder.DropTable(
                name: "StrategyGenerationCycleRun");

            migrationBuilder.DropTable(
                name: "StrategyGenerationFailure");

            migrationBuilder.DropTable(
                name: "StrategyGenerationFeedbackState");

            migrationBuilder.DropTable(
                name: "StrategyGenerationPendingArtifact");

            migrationBuilder.DropTable(
                name: "StrategyGenerationScheduleState");

            migrationBuilder.DropTable(
                name: "TradingSessionSchedules");

            migrationBuilder.DropIndex(
                name: "IX_EACommand_CreatedAt",
                table: "EACommand");

            migrationBuilder.DropIndex(
                name: "IX_EACommand_TargetInstanceId_Acknowledged",
                table: "EACommand");

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
                name: "PauseReason",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "PrunedAtUtc",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "RolloutEvaluationFailureCount",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "RolloutLastFailureMessage",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "ValidationPriority",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "ApprovalEvaluatedAt",
                table: "OptimizationRun");

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
                name: "CompletionPublicationPreparedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "CompletionPublicationStatus",
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
                name: "ExecutionStartedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "FollowUpLastCheckedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "FollowUpLastStatusCode",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "FollowUpLastStatusMessage",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "FollowUpRepairAttempts",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "FollowUpStatusUpdatedAt",
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

            migrationBuilder.DropColumn(
                name: "LifecycleReconciledAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "NextFollowUpCheckAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "QueuedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "ResultsPersistedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "LastProcessedDealSnapshotSequence",
                table: "EAInstance");

            migrationBuilder.DropColumn(
                name: "LastProcessedOrderSnapshotSequence",
                table: "EAInstance");

            migrationBuilder.DropColumn(
                name: "LastProcessedPositionSnapshotSequence",
                table: "EAInstance");

            migrationBuilder.DropColumn(
                name: "ValidationQueueKey",
                table: "BacktestRun");
        }
    }
}
