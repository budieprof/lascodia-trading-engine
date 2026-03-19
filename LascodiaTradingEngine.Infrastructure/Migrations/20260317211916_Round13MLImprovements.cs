using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Round13MLImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFallbackChampion",
                table: "MLModel",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MLShadowRegimeBreakdown",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShadowEvaluationId = table.Column<long>(type: "bigint", nullable: false),
                    Regime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    ChampionAccuracy = table.Column<decimal>(type: "numeric(8,6)", nullable: false),
                    ChallengerAccuracy = table.Column<decimal>(type: "numeric(8,6)", nullable: false),
                    AccuracyDelta = table.Column<decimal>(type: "numeric(8,6)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLShadowRegimeBreakdown", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLShadowRegimeBreakdown_MLShadowEvaluation_ShadowEvaluation~",
                        column: x => x.ShadowEvaluationId,
                        principalTable: "MLShadowEvaluation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLShadowRegimeBreakdown_ShadowEvaluationId_Regime",
                table: "MLShadowRegimeBreakdown",
                columns: new[] { "ShadowEvaluationId", "Regime" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLShadowRegimeBreakdown");

            migrationBuilder.DropColumn(
                name: "IsFallbackChampion",
                table: "MLModel");
        }
    }
}
