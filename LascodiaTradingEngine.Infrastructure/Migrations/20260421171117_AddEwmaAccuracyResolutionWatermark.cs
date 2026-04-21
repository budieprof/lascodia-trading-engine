using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEwmaAccuracyResolutionWatermark : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastOutcomeRecordedAt",
                table: "MLModelEwmaAccuracy",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastPredictionLogId",
                table: "MLModelEwmaAccuracy",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelEwmaAccuracy_MLModelId_LastOutcomeRecordedAt_LastPre~",
                table: "MLModelEwmaAccuracy",
                columns: new[] { "MLModelId", "LastOutcomeRecordedAt", "LastPredictionLogId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLModelEwmaAccuracy_MLModelId_LastOutcomeRecordedAt_LastPre~",
                table: "MLModelEwmaAccuracy");

            migrationBuilder.DropColumn(
                name: "LastOutcomeRecordedAt",
                table: "MLModelEwmaAccuracy");

            migrationBuilder.DropColumn(
                name: "LastPredictionLogId",
                table: "MLModelEwmaAccuracy");
        }
    }
}
