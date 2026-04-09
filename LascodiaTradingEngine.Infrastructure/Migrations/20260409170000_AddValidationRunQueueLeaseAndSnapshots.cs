using System;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    [DbContext(typeof(WriteApplicationDbContext))]
    [Migration("20260409170000_AddValidationRunQueueLeaseAndSnapshots")]
    public partial class AddValidationRunQueueLeaseAndSnapshots : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BacktestOptionsSnapshotJson",
                table: "BacktestRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "BacktestRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionLeaseExpiresAt",
                table: "BacktestRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionLeaseToken",
                table: "BacktestRun",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionStartedAt",
                table: "BacktestRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FinalBalance",
                table: "BacktestRun",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "BacktestRun",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeartbeatAt",
                table: "BacktestRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxDrawdownPct",
                table: "BacktestRun",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ProfitFactor",
                table: "BacktestRun",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QueuedAt",
                table: "BacktestRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QueueSource",
                table: "BacktestRun",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "BacktestRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "SharpeRatio",
                table: "BacktestRun",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalTrades",
                table: "BacktestRun",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalReturn",
                table: "BacktestRun",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WinRate",
                table: "BacktestRun",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BacktestOptionsSnapshotJson",
                table: "WalkForwardRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "WalkForwardRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionLeaseExpiresAt",
                table: "WalkForwardRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionLeaseToken",
                table: "WalkForwardRun",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionStartedAt",
                table: "WalkForwardRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "WalkForwardRun",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeartbeatAt",
                table: "WalkForwardRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QueuedAt",
                table: "WalkForwardRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QueueSource",
                table: "WalkForwardRun",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "WalkForwardRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE "BacktestRun"
                SET "QueuedAt" = COALESCE("StartedAt", CURRENT_TIMESTAMP)
                WHERE "QueuedAt" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "WalkForwardRun"
                SET "QueuedAt" = COALESCE("StartedAt", CURRENT_TIMESTAMP)
                WHERE "QueuedAt" IS NULL;
                """);

            migrationBuilder.AlterColumn<DateTime>(
                name: "QueuedAt",
                table: "BacktestRun",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "QueuedAt",
                table: "WalkForwardRun",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BacktestRun_CompletedRequiresCompletedAt",
                table: "BacktestRun",
                sql: "\"Status\" <> 'Completed' OR \"CompletedAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WalkForwardRun_PositiveWindowDays",
                table: "WalkForwardRun",
                sql: "\"InSampleDays\" > 0 AND \"OutOfSampleDays\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_Status_ExecutionLeaseExpiresAt",
                table: "BacktestRun",
                columns: new[] { "Status", "ExecutionLeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_Status_QueuedAt_Priority_Id",
                table: "BacktestRun",
                columns: new[] { "Status", "QueuedAt", "Priority", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_ActiveValidationQueueKey",
                table: "WalkForwardRun",
                column: "ValidationQueueKey",
                unique: true,
                filter: "\"ValidationQueueKey\" IS NOT NULL AND \"Status\" IN ('Queued','Running') AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_Status_ExecutionLeaseExpiresAt",
                table: "WalkForwardRun",
                columns: new[] { "Status", "ExecutionLeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_Status_QueuedAt_Priority_Id",
                table: "WalkForwardRun",
                columns: new[] { "Status", "QueuedAt", "Priority", "Id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_Status_ExecutionLeaseExpiresAt",
                table: "BacktestRun");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_Status_QueuedAt_Priority_Id",
                table: "BacktestRun");

            migrationBuilder.DropIndex(
                name: "IX_WalkForwardRun_ActiveValidationQueueKey",
                table: "WalkForwardRun");

            migrationBuilder.DropIndex(
                name: "IX_WalkForwardRun_Status_ExecutionLeaseExpiresAt",
                table: "WalkForwardRun");

            migrationBuilder.DropIndex(
                name: "IX_WalkForwardRun_Status_QueuedAt_Priority_Id",
                table: "WalkForwardRun");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BacktestRun_CompletedRequiresCompletedAt",
                table: "BacktestRun");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WalkForwardRun_PositiveWindowDays",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "BacktestOptionsSnapshotJson",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "ExecutionLeaseExpiresAt",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "ExecutionLeaseToken",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "ExecutionStartedAt",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "FinalBalance",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "MaxDrawdownPct",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "ProfitFactor",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "QueuedAt",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "QueueSource",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "SharpeRatio",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "TotalTrades",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "TotalReturn",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "WinRate",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "BacktestOptionsSnapshotJson",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "ExecutionLeaseExpiresAt",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "ExecutionLeaseToken",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "ExecutionStartedAt",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "QueuedAt",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "QueueSource",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "WalkForwardRun");
        }
    }
}
