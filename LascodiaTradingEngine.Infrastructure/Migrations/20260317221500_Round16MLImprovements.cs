using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Round16MLImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── MLModelHourlyAccuracy table ───────────────────────────────────
            migrationBuilder.CreateTable(
                name: "MLModelHourlyAccuracy",
                columns: table => new
                {
                    Id                 = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId          = table.Column<long>(type: "bigint", nullable: false),
                    Symbol             = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe          = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HourUtc            = table.Column<int>(type: "integer", nullable: false),
                    TotalPredictions   = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy           = table.Column<double>(type: "double precision", nullable: false),
                    WindowStart        = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt         = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted          = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId           = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelHourlyAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelHourlyAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHourlyAccuracy_MLModelId_HourUtc",
                table: "MLModelHourlyAccuracy",
                columns: new[] { "MLModelId", "HourUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHourlyAccuracy_Symbol_Timeframe_HourUtc",
                table: "MLModelHourlyAccuracy",
                columns: new[] { "Symbol", "Timeframe", "HourUtc" });

            // ── MLModelEwmaAccuracy table ─────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "MLModelEwmaAccuracy",
                columns: table => new
                {
                    Id               = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId        = table.Column<long>(type: "bigint", nullable: false),
                    Symbol           = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe        = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    EwmaAccuracy     = table.Column<double>(type: "double precision", nullable: false),
                    Alpha            = table.Column<double>(type: "double precision", nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    LastPredictionAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt       = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted        = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId         = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelEwmaAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelEwmaAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelEwmaAccuracy_MLModelId",
                table: "MLModelEwmaAccuracy",
                column: "MLModelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelEwmaAccuracy_Symbol_Timeframe",
                table: "MLModelEwmaAccuracy",
                columns: new[] { "Symbol", "Timeframe" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MLModelHourlyAccuracy");
            migrationBuilder.DropTable(name: "MLModelEwmaAccuracy");
        }
    }
}
