using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Round14MLImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LabelImbalanceRatio",
                table: "MLTrainingRun",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrainingDatasetStatsJson",
                table: "MLTrainingRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LiveActiveDays",
                table: "MLModel",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "LiveDirectionAccuracy",
                table: "MLModel",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LiveTotalPredictions",
                table: "MLModel",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MLModelSessionAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Session = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
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
                    table.PrimaryKey("PK_MLModelSessionAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelSessionAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelSessionAccuracy_MLModelId_Session",
                table: "MLModelSessionAccuracy",
                columns: new[] { "MLModelId", "Session" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelSessionAccuracy_Symbol_Timeframe_Session",
                table: "MLModelSessionAccuracy",
                columns: new[] { "Symbol", "Timeframe", "Session" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLModelSessionAccuracy");

            migrationBuilder.DropColumn(
                name: "LabelImbalanceRatio",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "TrainingDatasetStatsJson",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "LiveActiveDays",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "LiveDirectionAccuracy",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "LiveTotalPredictions",
                table: "MLModel");
        }
    }
}
