using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizationWorkerAPlusOperationalPolish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RolloutHistoryJson",
                table: "Strategy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FollowUpLastStatusCode",
                table: "OptimizationRun",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FollowUpLastStatusMessage",
                table: "OptimizationRun",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FollowUpStatusUpdatedAt",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_FollowUpStatusUpdatedAt",
                table: "OptimizationRun",
                columns: new[] { "Status", "FollowUpStatusUpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_Status_FollowUpStatusUpdatedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "RolloutHistoryJson",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "FollowUpLastStatusCode",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "FollowUpLastStatusMessage",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "FollowUpStatusUpdatedAt",
                table: "OptimizationRun");
        }
    }
}
