using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Round12MLImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "MLTrainingRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxAttempts",
                table: "MLTrainingRun",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAt",
                table: "MLTrainingRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MLModelRegimeAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Regime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelRegimeAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelRegimeAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelPredictionLog_TradeSignalId_MLModelId",
                table: "MLModelPredictionLog",
                columns: new[] { "TradeSignalId", "MLModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelRegimeAccuracy_MLModelId_Regime",
                table: "MLModelRegimeAccuracy",
                columns: new[] { "MLModelId", "Regime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelRegimeAccuracy_Symbol_Timeframe_Regime",
                table: "MLModelRegimeAccuracy",
                columns: new[] { "Symbol", "Timeframe", "Regime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLModelRegimeAccuracy");

            migrationBuilder.DropIndex(
                name: "IX_MLModelPredictionLog_TradeSignalId_MLModelId",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "MaxAttempts",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "MLTrainingRun");
        }
    }
}
