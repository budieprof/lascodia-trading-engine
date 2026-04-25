using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMLAdaptiveThresholdLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLAdaptiveThresholdLog",
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
                    PreviousThreshold = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    OptimalThreshold = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    NewThreshold = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    Drift = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    HoldoutEvAtNewThreshold = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    HoldoutEvAtPreviousThreshold = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    HoldoutMeanPnlPips = table.Column<double>(type: "double precision", precision: 14, scale: 6, nullable: false),
                    SweepSampleSize = table.Column<int>(type: "integer", nullable: false),
                    HoldoutSampleSize = table.Column<int>(type: "integer", nullable: false),
                    StationarityPsi = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    DiagnosticsJson = table.Column<string>(type: "text", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdaptiveThresholdLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLAdaptiveThresholdLog_MLModelId_EvaluatedAt",
                table: "MLAdaptiveThresholdLog",
                columns: new[] { "MLModelId", "EvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLAdaptiveThresholdLog_Outcome_Reason_EvaluatedAt",
                table: "MLAdaptiveThresholdLog",
                columns: new[] { "Outcome", "Reason", "EvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLAdaptiveThresholdLog_Symbol_Timeframe_Regime_EvaluatedAt",
                table: "MLAdaptiveThresholdLog",
                columns: new[] { "Symbol", "Timeframe", "Regime", "EvaluatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLAdaptiveThresholdLog");
        }
    }
}
