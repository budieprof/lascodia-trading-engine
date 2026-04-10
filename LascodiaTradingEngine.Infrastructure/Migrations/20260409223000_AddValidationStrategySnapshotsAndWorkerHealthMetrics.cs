using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    [DbContext(typeof(WriteApplicationDbContext))]
    [Migration("20260409223000_AddValidationStrategySnapshotsAndWorkerHealthMetrics")]
    public partial class AddValidationStrategySnapshotsAndWorkerHealthMetrics : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StrategySnapshotJson",
                table: "BacktestRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StrategySnapshotJson",
                table: "WalkForwardRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExecutionDurationP50Ms",
                table: "WorkerHealthSnapshot",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ExecutionDurationP95Ms",
                table: "WorkerHealthSnapshot",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LastExecutionDurationMs",
                table: "WorkerHealthSnapshot",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LastQueueLatencyMs",
                table: "WorkerHealthSnapshot",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "QueueLatencyP50Ms",
                table: "WorkerHealthSnapshot",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "QueueLatencyP95Ms",
                table: "WorkerHealthSnapshot",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "RecoveriesLastHour",
                table: "WorkerHealthSnapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RetriesLastHour",
                table: "WorkerHealthSnapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE "BacktestRun"
                SET
                    "TotalTrades" = COALESCE("TotalTrades", NULLIF(("ResultJson"::jsonb ->> 'TotalTrades'), '')::integer),
                    "WinRate" = COALESCE("WinRate", NULLIF(("ResultJson"::jsonb ->> 'WinRate'), '')::numeric(18,6)),
                    "ProfitFactor" = COALESCE("ProfitFactor", NULLIF(("ResultJson"::jsonb ->> 'ProfitFactor'), '')::numeric(18,6)),
                    "MaxDrawdownPct" = COALESCE("MaxDrawdownPct", NULLIF(("ResultJson"::jsonb ->> 'MaxDrawdownPct'), '')::numeric(18,6)),
                    "SharpeRatio" = COALESCE("SharpeRatio", NULLIF(("ResultJson"::jsonb ->> 'SharpeRatio'), '')::numeric(18,6)),
                    "FinalBalance" = COALESCE("FinalBalance", NULLIF(("ResultJson"::jsonb ->> 'FinalBalance'), '')::numeric(18,2)),
                    "TotalReturn" = COALESCE("TotalReturn", NULLIF(("ResultJson"::jsonb ->> 'TotalReturn'), '')::numeric(18,6))
                WHERE "ResultJson" IS NOT NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StrategySnapshotJson",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "StrategySnapshotJson",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "ExecutionDurationP50Ms",
                table: "WorkerHealthSnapshot");

            migrationBuilder.DropColumn(
                name: "ExecutionDurationP95Ms",
                table: "WorkerHealthSnapshot");

            migrationBuilder.DropColumn(
                name: "LastExecutionDurationMs",
                table: "WorkerHealthSnapshot");

            migrationBuilder.DropColumn(
                name: "LastQueueLatencyMs",
                table: "WorkerHealthSnapshot");

            migrationBuilder.DropColumn(
                name: "QueueLatencyP50Ms",
                table: "WorkerHealthSnapshot");

            migrationBuilder.DropColumn(
                name: "QueueLatencyP95Ms",
                table: "WorkerHealthSnapshot");

            migrationBuilder.DropColumn(
                name: "RecoveriesLastHour",
                table: "WorkerHealthSnapshot");

            migrationBuilder.DropColumn(
                name: "RetriesLastHour",
                table: "WorkerHealthSnapshot");
        }
    }
}
