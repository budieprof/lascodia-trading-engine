using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConformalBreakerCoverageFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConformalPredictionSetJson",
                table: "MLModelPredictionLog",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ConformalTargetCoverageUsed",
                table: "MLModelPredictionLog",
                type: "double precision",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ConformalThresholdUsed",
                table: "MLModelPredictionLog",
                type: "double precision",
                precision: 10,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MLConformalCalibrationId",
                table: "MLModelPredictionLog",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasConformalCovered",
                table: "MLModelPredictionLog",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CoverageLowerBound",
                table: "MLConformalBreakerLog",
                type: "double precision",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CoveragePValue",
                table: "MLConformalBreakerLog",
                type: "double precision",
                precision: 18,
                scale: 12,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CoverageThreshold",
                table: "MLConformalBreakerLog",
                type: "double precision",
                precision: 10,
                scale: 8,
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "CoveredCount",
                table: "MLConformalBreakerLog",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SampleCount",
                table: "MLConformalBreakerLog",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "TargetCoverage",
                table: "MLConformalBreakerLog",
                type: "double precision",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "TripReason",
                table: "MLConformalBreakerLog",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_MLModelPredictionLog_MLConformalCalibrationId",
                table: "MLModelPredictionLog",
                column: "MLConformalCalibrationId");

            migrationBuilder.CreateIndex(
                name: "IX_MLModelPredictionLog_MLModelId_OutcomeRecordedAt",
                table: "MLModelPredictionLog",
                columns: new[] { "MLModelId", "OutcomeRecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelPredictionLog_MLModelId_WasConformalCovered_OutcomeR~",
                table: "MLModelPredictionLog",
                columns: new[] { "MLModelId", "WasConformalCovered", "OutcomeRecordedAt" },
                filter: "\"OutcomeRecordedAt\" IS NOT NULL AND \"IsDeleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalBreakerLog_MLModelId_Symbol_Timeframe",
                table: "MLConformalBreakerLog",
                columns: new[] { "MLModelId", "Symbol", "Timeframe" },
                unique: true,
                filter: "\"IsActive\" = TRUE AND \"IsDeleted\" = FALSE");

            migrationBuilder.AddForeignKey(
                name: "FK_MLModelPredictionLog_MLConformalCalibration_MLConformalCali~",
                table: "MLModelPredictionLog",
                column: "MLConformalCalibrationId",
                principalTable: "MLConformalCalibration",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MLModelPredictionLog_MLConformalCalibration_MLConformalCali~",
                table: "MLModelPredictionLog");

            migrationBuilder.DropIndex(
                name: "IX_MLModelPredictionLog_MLConformalCalibrationId",
                table: "MLModelPredictionLog");

            migrationBuilder.DropIndex(
                name: "IX_MLModelPredictionLog_MLModelId_OutcomeRecordedAt",
                table: "MLModelPredictionLog");

            migrationBuilder.DropIndex(
                name: "IX_MLModelPredictionLog_MLModelId_WasConformalCovered_OutcomeR~",
                table: "MLModelPredictionLog");

            migrationBuilder.DropIndex(
                name: "IX_MLConformalBreakerLog_MLModelId_Symbol_Timeframe",
                table: "MLConformalBreakerLog");

            migrationBuilder.DropColumn(
                name: "ConformalPredictionSetJson",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "ConformalTargetCoverageUsed",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "ConformalThresholdUsed",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "MLConformalCalibrationId",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "WasConformalCovered",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "CoverageLowerBound",
                table: "MLConformalBreakerLog");

            migrationBuilder.DropColumn(
                name: "CoveragePValue",
                table: "MLConformalBreakerLog");

            migrationBuilder.DropColumn(
                name: "CoverageThreshold",
                table: "MLConformalBreakerLog");

            migrationBuilder.DropColumn(
                name: "CoveredCount",
                table: "MLConformalBreakerLog");

            migrationBuilder.DropColumn(
                name: "SampleCount",
                table: "MLConformalBreakerLog");

            migrationBuilder.DropColumn(
                name: "TargetCoverage",
                table: "MLConformalBreakerLog");

            migrationBuilder.DropColumn(
                name: "TripReason",
                table: "MLConformalBreakerLog");
        }
    }
}
