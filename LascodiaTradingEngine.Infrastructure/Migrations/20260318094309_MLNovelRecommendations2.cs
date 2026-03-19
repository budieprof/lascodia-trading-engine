using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdversarialAugmentApplied",
                table: "MLTrainingRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMamlRun",
                table: "MLTrainingRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "LabelNoiseRatePercent",
                table: "MLTrainingRun",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MamlInnerSteps",
                table: "MLTrainingRun",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SmoteApplied",
                table: "MLTrainingRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "SparsityPercent",
                table: "MLTrainingRun",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TemporalDecayHalfLifeDays",
                table: "MLTrainingRun",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ConformalNonConformityScore",
                table: "MLModelPredictionLog",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOod",
                table: "MLModelPredictionLog",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "MagnitudeP10Pips",
                table: "MLModelPredictionLog",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MagnitudeP90Pips",
                table: "MLModelPredictionLog",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OodMahalanobisScore",
                table: "MLModelPredictionLog",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegimeRoutingDecision",
                table: "MLModelPredictionLog",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShapValuesJson",
                table: "MLModelPredictionLog",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsMamlInitializer",
                table: "MLModel",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMetaLearner",
                table: "MLModel",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastOnlineLearningAt",
                table: "MLModel",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OnlineLearningUpdateCount",
                table: "MLModel",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MLConformalCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    NonConformityScoresJson = table.Column<string>(type: "text", nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    CoverageAlpha = table.Column<double>(type: "double precision", precision: 5, scale: 4, nullable: false),
                    CoverageThreshold = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    EmpiricalCoverage = table.Column<double>(type: "double precision", precision: 5, scale: 4, nullable: true),
                    AmbiguousRate = table.Column<double>(type: "double precision", precision: 5, scale: 4, nullable: true),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConformalCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLConformalCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLFeatureInteractionAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FeatureIndexA = table.Column<int>(type: "integer", nullable: false),
                    FeatureNameA = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FeatureIndexB = table.Column<int>(type: "integer", nullable: false),
                    FeatureNameB = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    InteractionScore = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    IsIncludedAsFeature = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFeatureInteractionAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLFeatureInteractionAudit_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLFeatureNormStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Regime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MeansJson = table.Column<string>(type: "text", nullable: false),
                    StdsJson = table.Column<string>(type: "text", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFeatureNormStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHawkesKernelParams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Mu = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Alpha = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Beta = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", precision: 18, scale: 4, nullable: true),
                    SuppressMultiplier = table.Column<double>(type: "double precision", precision: 5, scale: 2, nullable: false),
                    FitSamples = table.Column<int>(type: "integer", nullable: false),
                    FittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHawkesKernelParams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHyperparamPrior",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ObservationsJson = table.Column<string>(type: "text", nullable: false),
                    TotalObservations = table.Column<int>(type: "integer", nullable: false),
                    GoodFraction = table.Column<double>(type: "double precision", precision: 5, scale: 4, nullable: false),
                    BestObjective = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    BestConfigJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHyperparamPrior", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMinTReconciliationLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RawProbabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    ReconciledProbabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    ReconciledH1Probability = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    PreReconciliationDisagreement = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    TimeframeCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMinTReconciliationLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPredictionScorePsiLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    WeekStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PsiValue = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    CurrentWeekCount = table.Column<int>(type: "integer", nullable: false),
                    ReferenceCount = table.Column<int>(type: "integer", nullable: false),
                    CurrentMeanProb = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    ReferenceMeanProb = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    IsSignificantShift = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPredictionScorePsiLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLPredictionScorePsiLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLStackingMetaModel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    BaseModelIdsJson = table.Column<string>(type: "text", nullable: false),
                    BaseModelCount = table.Column<int>(type: "integer", nullable: false),
                    MetaWeightsJson = table.Column<string>(type: "text", nullable: false),
                    MetaBias = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    DirectionAccuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    BrierScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStackingMetaModel", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalCalibration_MLModelId_IsDeleted",
                table: "MLConformalCalibration",
                columns: new[] { "MLModelId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalCalibration_Symbol_Timeframe",
                table: "MLConformalCalibration",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureInteractionAudit_MLModelId_IsIncludedAsFeature",
                table: "MLFeatureInteractionAudit",
                columns: new[] { "MLModelId", "IsIncludedAsFeature" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureInteractionAudit_MLModelId_Rank",
                table: "MLFeatureInteractionAudit",
                columns: new[] { "MLModelId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureNormStats_Symbol_Timeframe_Regime",
                table: "MLFeatureNormStats",
                columns: new[] { "Symbol", "Timeframe", "Regime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLHawkesKernelParams_Symbol_Timeframe_FittedAt",
                table: "MLHawkesKernelParams",
                columns: new[] { "Symbol", "Timeframe", "FittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLHyperparamPrior_Symbol_Timeframe",
                table: "MLHyperparamPrior",
                columns: new[] { "Symbol", "Timeframe" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLMinTReconciliationLog_Symbol_ComputedAt",
                table: "MLMinTReconciliationLog",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLPredictionScorePsiLog_MLModelId_WeekStartDate",
                table: "MLPredictionScorePsiLog",
                columns: new[] { "MLModelId", "WeekStartDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLPredictionScorePsiLog_Symbol_IsSignificantShift",
                table: "MLPredictionScorePsiLog",
                columns: new[] { "Symbol", "IsSignificantShift" });

            migrationBuilder.CreateIndex(
                name: "IX_MLStackingMetaModel_Symbol_Timeframe_IsActive",
                table: "MLStackingMetaModel",
                columns: new[] { "Symbol", "Timeframe", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLConformalCalibration");

            migrationBuilder.DropTable(
                name: "MLFeatureInteractionAudit");

            migrationBuilder.DropTable(
                name: "MLFeatureNormStats");

            migrationBuilder.DropTable(
                name: "MLHawkesKernelParams");

            migrationBuilder.DropTable(
                name: "MLHyperparamPrior");

            migrationBuilder.DropTable(
                name: "MLMinTReconciliationLog");

            migrationBuilder.DropTable(
                name: "MLPredictionScorePsiLog");

            migrationBuilder.DropTable(
                name: "MLStackingMetaModel");

            migrationBuilder.DropColumn(
                name: "AdversarialAugmentApplied",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "IsMamlRun",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "LabelNoiseRatePercent",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "MamlInnerSteps",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "SmoteApplied",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "SparsityPercent",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "TemporalDecayHalfLifeDays",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "ConformalNonConformityScore",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "IsOod",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "MagnitudeP10Pips",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "MagnitudeP90Pips",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "OodMahalanobisScore",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "RegimeRoutingDecision",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "ShapValuesJson",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "IsMamlInitializer",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "IsMetaLearner",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "LastOnlineLearningAt",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "OnlineLearningUpdateCount",
                table: "MLModel");
        }
    }
}
