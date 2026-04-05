using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOptimizationRunIntermediateResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReOptimizePerFold",
                table: "WalkForwardRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ScreeningMetricsJson",
                table: "Strategy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntermediateResultsJson",
                table: "OptimizationRun",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "BacktestRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "StrategyRegimeParams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Regime = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    HealthScore = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    HealthScoreCILower = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    OptimizationRunId = table.Column<long>(type: "bigint", nullable: true),
                    OptimizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyRegimeParams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrategyRegimeParams_OptimizationRun_OptimizationRunId",
                        column: x => x.OptimizationRunId,
                        principalTable: "OptimizationRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StrategyRegimeParams_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyRegimeParams_OptimizationRunId",
                table: "StrategyRegimeParams",
                column: "OptimizationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyRegimeParams_StrategyId_Regime",
                table: "StrategyRegimeParams",
                columns: new[] { "StrategyId", "Regime" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StrategyRegimeParams");

            migrationBuilder.DropColumn(
                name: "ReOptimizePerFold",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "ScreeningMetricsJson",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "IntermediateResultsJson",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "BacktestRun");
        }
    }
}
