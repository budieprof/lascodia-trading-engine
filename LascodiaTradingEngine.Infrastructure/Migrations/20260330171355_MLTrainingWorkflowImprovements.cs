using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLTrainingWorkflowImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AbstentionPrecision",
                table: "MLTrainingRun",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AbstentionRate",
                table: "MLTrainingRun",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DriftMetadataJson",
                table: "MLTrainingRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DriftTriggerType",
                table: "MLTrainingRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "MLTrainingRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "TournamentGroupId",
                table: "MLShadowEvaluation",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TournamentRank",
                table: "MLShadowEvaluation",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CommitteeDisagreement",
                table: "MLModelPredictionLog",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommitteeModelIdsJson",
                table: "MLModelPredictionLog",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ServedCalibratedProbability",
                table: "MLModelPredictionLog",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastChallengedAt",
                table: "MLModel",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MLCorrelatedFailureLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FailingModelCount = table.Column<int>(type: "integer", nullable: false),
                    TotalModelCount = table.Column<int>(type: "integer", nullable: false),
                    FailureRatio = table.Column<double>(type: "double precision", nullable: false),
                    SymbolsAffectedJson = table.Column<string>(type: "text", nullable: false),
                    PauseActivated = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCorrelatedFailureLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFeatureConsensusSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FeatureConsensusJson = table.Column<string>(type: "text", nullable: false),
                    ContributingModelCount = table.Column<int>(type: "integer", nullable: false),
                    MeanKendallTau = table.Column<double>(type: "double precision", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFeatureConsensusSnapshot", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLCorrelatedFailureLog_DetectedAt",
                table: "MLCorrelatedFailureLog",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureConsensusSnapshot_Symbol_Timeframe_DetectedAt",
                table: "MLFeatureConsensusSnapshot",
                columns: new[] { "Symbol", "Timeframe", "DetectedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLCorrelatedFailureLog");

            migrationBuilder.DropTable(
                name: "MLFeatureConsensusSnapshot");

            migrationBuilder.DropColumn(
                name: "AbstentionPrecision",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "AbstentionRate",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "DriftMetadataJson",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "DriftTriggerType",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "TournamentGroupId",
                table: "MLShadowEvaluation");

            migrationBuilder.DropColumn(
                name: "TournamentRank",
                table: "MLShadowEvaluation");

            migrationBuilder.DropColumn(
                name: "CommitteeDisagreement",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "CommitteeModelIdsJson",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "ServedCalibratedProbability",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "LastChallengedAt",
                table: "MLModel");
        }
    }
}
