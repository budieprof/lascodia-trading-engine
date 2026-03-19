using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Round15MLImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── MLModelPredictionLog: add LatencyMs ───────────────────────────
            migrationBuilder.AddColumn<int>(
                name: "LatencyMs",
                table: "MLModelPredictionLog",
                type: "integer",
                nullable: true);

            // ── MLModelVolatilityAccuracy table ───────────────────────────────
            migrationBuilder.CreateTable(
                name: "MLModelVolatilityAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId          = table.Column<long>(type: "bigint", nullable: false),
                    Symbol             = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe          = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    VolatilityBucket   = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalPredictions   = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy           = table.Column<double>(type: "double precision", nullable: false),
                    AtrThresholdLow    = table.Column<decimal>(type: "numeric", nullable: false),
                    AtrThresholdHigh   = table.Column<decimal>(type: "numeric", nullable: false),
                    WindowStart        = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt         = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted          = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId           = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelVolatilityAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelVolatilityAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelVolatilityAccuracy_MLModelId_VolatilityBucket",
                table: "MLModelVolatilityAccuracy",
                columns: new[] { "MLModelId", "VolatilityBucket" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelVolatilityAccuracy_Symbol_Timeframe_VolatilityBucket",
                table: "MLModelVolatilityAccuracy",
                columns: new[] { "Symbol", "Timeframe", "VolatilityBucket" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLModelVolatilityAccuracy");

            migrationBuilder.DropColumn(
                name: "LatencyMs",
                table: "MLModelPredictionLog");
        }
    }
}
