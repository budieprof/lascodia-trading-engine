using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCalibrationSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalibrationSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PeriodGranularity = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RejectionCount = table.Column<long>(type: "bigint", nullable: false),
                    DistinctSymbols = table.Column<int>(type: "integer", nullable: false),
                    DistinctStrategies = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalibrationSnapshot", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalibrationSnapshot_Period_Stage_Reason_Unique",
                table: "CalibrationSnapshot",
                columns: new[] { "PeriodStart", "PeriodGranularity", "Stage", "Reason" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalibrationSnapshot_PeriodStart",
                table: "CalibrationSnapshot",
                column: "PeriodStart");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalibrationSnapshot");
        }
    }
}
