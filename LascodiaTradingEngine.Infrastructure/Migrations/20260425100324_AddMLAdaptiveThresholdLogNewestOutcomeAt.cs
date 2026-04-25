using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMLAdaptiveThresholdLogNewestOutcomeAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NewestOutcomeAt",
                table: "MLAdaptiveThresholdLog",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLAdaptiveThresholdLog_MLModelId_NewestOutcomeAt",
                table: "MLAdaptiveThresholdLog",
                columns: new[] { "MLModelId", "NewestOutcomeAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLAdaptiveThresholdLog_MLModelId_NewestOutcomeAt",
                table: "MLAdaptiveThresholdLog");

            migrationBuilder.DropColumn(
                name: "NewestOutcomeAt",
                table: "MLAdaptiveThresholdLog");
        }
    }
}
