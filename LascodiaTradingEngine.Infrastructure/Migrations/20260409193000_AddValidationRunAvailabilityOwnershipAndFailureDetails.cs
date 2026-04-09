using System;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    [DbContext(typeof(WriteApplicationDbContext))]
    [Migration("20260409193000_AddValidationRunAvailabilityOwnershipAndFailureDetails")]
    public partial class AddValidationRunAvailabilityOwnershipAndFailureDetails : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AvailableAt",
                table: "BacktestRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByWorkerId",
                table: "BacktestRun",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureDetailsJson",
                table: "BacktestRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAttemptAt",
                table: "BacktestRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AvailableAt",
                table: "WalkForwardRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByWorkerId",
                table: "WalkForwardRun",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureDetailsJson",
                table: "WalkForwardRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAttemptAt",
                table: "WalkForwardRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "BacktestRun"
                SET "AvailableAt" = COALESCE("QueuedAt", "StartedAt", CURRENT_TIMESTAMP)
                WHERE "AvailableAt" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "WalkForwardRun"
                SET "AvailableAt" = COALESCE("QueuedAt", "StartedAt", CURRENT_TIMESTAMP)
                WHERE "AvailableAt" IS NULL;
                """);

            migrationBuilder.AlterColumn<DateTime>(
                name: "AvailableAt",
                table: "BacktestRun",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "AvailableAt",
                table: "WalkForwardRun",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_Status_QueuedAt_Priority_Id",
                table: "BacktestRun");

            migrationBuilder.DropIndex(
                name: "IX_WalkForwardRun_Status_QueuedAt_Priority_Id",
                table: "WalkForwardRun");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_Status_AvailableAt_Priority_QueuedAt_Id",
                table: "BacktestRun",
                columns: new[] { "Status", "AvailableAt", "Priority", "QueuedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_Status_AvailableAt_Priority_QueuedAt_Id",
                table: "WalkForwardRun",
                columns: new[] { "Status", "AvailableAt", "Priority", "QueuedAt", "Id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_Status_AvailableAt_Priority_QueuedAt_Id",
                table: "BacktestRun");

            migrationBuilder.DropIndex(
                name: "IX_WalkForwardRun_Status_AvailableAt_Priority_QueuedAt_Id",
                table: "WalkForwardRun");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_Status_QueuedAt_Priority_Id",
                table: "BacktestRun",
                columns: new[] { "Status", "QueuedAt", "Priority", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_Status_QueuedAt_Priority_Id",
                table: "WalkForwardRun",
                columns: new[] { "Status", "QueuedAt", "Priority", "Id" });

            migrationBuilder.DropColumn(
                name: "AvailableAt",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "ClaimedByWorkerId",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "FailureDetailsJson",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "AvailableAt",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "ClaimedByWorkerId",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "FailureDetailsJson",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "WalkForwardRun");
        }
    }
}
