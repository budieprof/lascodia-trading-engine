using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Round17MLImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── MLModelMagnitudeStats table ───────────────────────────────────
            migrationBuilder.CreateTable(
                name: "MLModelMagnitudeStats",
                columns: table => new
                {
                    Id                     = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId              = table.Column<long>(type: "bigint", nullable: false),
                    Symbol                 = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe              = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    MeanAbsoluteError      = table.Column<double>(type: "double precision", nullable: false),
                    CorrelationCoefficient = table.Column<double>(type: "double precision", nullable: false),
                    MeanSignedBias         = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount            = table.Column<int>(type: "integer", nullable: false),
                    WindowStart            = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt             = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted              = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId               = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelMagnitudeStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelMagnitudeStats_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelMagnitudeStats_MLModelId",
                table: "MLModelMagnitudeStats",
                column: "MLModelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelMagnitudeStats_Symbol_Timeframe",
                table: "MLModelMagnitudeStats",
                columns: new[] { "Symbol", "Timeframe" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MLModelMagnitudeStats");
        }
    }
}
