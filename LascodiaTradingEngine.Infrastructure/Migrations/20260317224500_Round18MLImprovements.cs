using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Round18MLImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── MLModelHorizonAccuracy table ──────────────────────────────────
            migrationBuilder.CreateTable(
                name: "MLModelHorizonAccuracy",
                columns: table => new
                {
                    Id                 = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId          = table.Column<long>(type: "bigint", nullable: false),
                    Symbol             = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe          = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HorizonBars        = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("PK_MLModelHorizonAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelHorizonAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHorizonAccuracy_MLModelId_HorizonBars",
                table: "MLModelHorizonAccuracy",
                columns: new[] { "MLModelId", "HorizonBars" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHorizonAccuracy_Symbol_Timeframe_HorizonBars",
                table: "MLModelHorizonAccuracy",
                columns: new[] { "Symbol", "Timeframe", "HorizonBars" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MLModelHorizonAccuracy");
        }
    }
}
