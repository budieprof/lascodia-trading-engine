using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConformalBreakerAuditDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CoverageUpperBound",
                table: "MLConformalBreakerLog",
                type: "double precision",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FreshSampleCount",
                table: "MLConformalBreakerLog",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEvaluatedOutcomeAt",
                table: "MLConformalBreakerLog",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelPredictionLog_ConformalBreakerRecent",
                table: "MLModelPredictionLog",
                columns: new[] { "MLModelId", "OutcomeRecordedAt", "Id" },
                filter: "\"OutcomeRecordedAt\" IS NOT NULL AND \"ActualDirection\" IS NOT NULL AND \"IsDeleted\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLModelPredictionLog_ConformalBreakerRecent",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "CoverageUpperBound",
                table: "MLConformalBreakerLog");

            migrationBuilder.DropColumn(
                name: "FreshSampleCount",
                table: "MLConformalBreakerLog");

            migrationBuilder.DropColumn(
                name: "LastEvaluatedOutcomeAt",
                table: "MLConformalBreakerLog");
        }
    }
}
