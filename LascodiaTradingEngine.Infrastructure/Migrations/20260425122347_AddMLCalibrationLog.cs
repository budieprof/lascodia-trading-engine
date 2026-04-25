using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMLCalibrationLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLCalibrationLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    Regime = table.Column<int>(type: "integer", nullable: true),
                    EvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResolvedSampleCount = table.Column<int>(type: "integer", nullable: false),
                    CurrentEce = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    PreviousEce = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: true),
                    BaselineEce = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: true),
                    TrendDelta = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    BaselineDelta = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    MeanConfidence = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    EceStderr = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    ThresholdExceeded = table.Column<bool>(type: "boolean", nullable: false),
                    TrendExceeded = table.Column<bool>(type: "boolean", nullable: false),
                    BaselineExceeded = table.Column<bool>(type: "boolean", nullable: false),
                    AlertState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NewestOutcomeAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DiagnosticsJson = table.Column<string>(type: "text", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCalibrationLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLCalibrationLog_AlertState_EvaluatedAt",
                table: "MLCalibrationLog",
                columns: new[] { "AlertState", "EvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCalibrationLog_MLModelId_EvaluatedAt",
                table: "MLCalibrationLog",
                columns: new[] { "MLModelId", "EvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCalibrationLog_MLModelId_NewestOutcomeAt",
                table: "MLCalibrationLog",
                columns: new[] { "MLModelId", "NewestOutcomeAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCalibrationLog_Symbol_Timeframe_Regime_EvaluatedAt",
                table: "MLCalibrationLog",
                columns: new[] { "Symbol", "Timeframe", "Regime", "EvaluatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLCalibrationLog");
        }
    }
}
