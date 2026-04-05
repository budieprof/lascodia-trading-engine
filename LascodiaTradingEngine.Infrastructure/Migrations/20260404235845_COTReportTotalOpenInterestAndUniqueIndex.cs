using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class COTReportTotalOpenInterestAndUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_COTReport_Currency_ReportDate",
                table: "COTReport");

            migrationBuilder.AddColumn<long>(
                name: "TotalOpenInterest",
                table: "COTReport",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "SpreadProfiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HourUtc = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: true),
                    SessionName = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SpreadP25 = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SpreadP50 = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SpreadP75 = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SpreadP95 = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SpreadMean = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    AggregatedFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AggregatedTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpreadProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_COTReport_Currency_ReportDate",
                table: "COTReport",
                columns: new[] { "Currency", "ReportDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpreadProfile_Symbol_Hour_DOW",
                table: "SpreadProfiles",
                columns: new[] { "Symbol", "HourUtc", "DayOfWeek" });

            migrationBuilder.CreateIndex(
                name: "IX_SpreadProfiles_Symbol",
                table: "SpreadProfiles",
                column: "Symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpreadProfiles");

            migrationBuilder.DropIndex(
                name: "IX_COTReport_Currency_ReportDate",
                table: "COTReport");

            migrationBuilder.DropColumn(
                name: "TotalOpenInterest",
                table: "COTReport");

            migrationBuilder.CreateIndex(
                name: "IX_COTReport_Currency_ReportDate",
                table: "COTReport",
                columns: new[] { "Currency", "ReportDate" });
        }
    }
}
