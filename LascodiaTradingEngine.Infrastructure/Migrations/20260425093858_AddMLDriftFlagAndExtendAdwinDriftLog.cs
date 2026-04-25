using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMLDriftFlagAndExtendAdwinDriftLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AccuracyDrop",
                table: "MLAdwinDriftLog",
                type: "double precision",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "DeltaUsed",
                table: "MLAdwinDriftLog",
                type: "double precision",
                precision: 10,
                scale: 8,
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "DominantRegime",
                table: "MLAdwinDriftLog",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "OutcomeSeriesCompressed",
                table: "MLAdwinDriftLog",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MLDriftFlag",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DetectorType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstDetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRefreshedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsecutiveDetections = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDriftFlag", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_MLTrainingRun_Active_Per_Pair",
                table: "MLTrainingRun",
                columns: new[] { "Symbol", "Timeframe" },
                unique: true,
                filter: "\"Status\" IN ('Queued','Running') AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_MLAdwinDriftLog_DetectedAt",
                table: "MLAdwinDriftLog",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLDriftFlag_DetectorType_ExpiresAtUtc",
                table: "MLDriftFlag",
                columns: new[] { "DetectorType", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MLDriftFlag_Symbol_Timeframe_DetectorType",
                table: "MLDriftFlag",
                columns: new[] { "Symbol", "Timeframe", "DetectorType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLDriftFlag");

            migrationBuilder.DropIndex(
                name: "UX_MLTrainingRun_Active_Per_Pair",
                table: "MLTrainingRun");

            migrationBuilder.DropIndex(
                name: "IX_MLAdwinDriftLog_DetectedAt",
                table: "MLAdwinDriftLog");

            migrationBuilder.DropColumn(
                name: "AccuracyDrop",
                table: "MLAdwinDriftLog");

            migrationBuilder.DropColumn(
                name: "DeltaUsed",
                table: "MLAdwinDriftLog");

            migrationBuilder.DropColumn(
                name: "DominantRegime",
                table: "MLAdwinDriftLog");

            migrationBuilder.DropColumn(
                name: "OutcomeSeriesCompressed",
                table: "MLAdwinDriftLog");
        }
    }
}
