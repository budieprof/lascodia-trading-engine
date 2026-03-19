using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations7 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLActivationMaxLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    LearnerIndex = table.Column<int>(type: "integer", nullable: false),
                    MaxActivation = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    MaxActivationFeaturesJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLActivationMaxLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLActivationMaxLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLBayesThresholdLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    OptimalThreshold = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ExpectedImprovement = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    GPMeanAtOptimum = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    TrialsCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBayesThresholdLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLBayesThresholdLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLC51Distribution",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    NumAtoms = table.Column<int>(type: "integer", nullable: false),
                    VMin = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    VMax = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    AtomProbsJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    ExpectedValue = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Var95 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLC51Distribution", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLC51Distribution_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLCashSelectionLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    SelectedTrainerKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BestValidationAccuracy = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    BicPenalty = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CandidateResultsJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    SelectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCashSelectionLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCashSelectionLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLContrastiveEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    ProjectionWeightsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    ProjectionBiasJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    ContrastiveLoss = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLContrastiveEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCrcCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    LossFunction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Alpha = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    LambdaHat = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EmpiricalRisk = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCrcCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCrcCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLEmdDriftLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    WassersteinDistance = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    BaselinePeriodDays = table.Column<int>(type: "integer", nullable: false),
                    CurrentWindowDays = table.Column<int>(type: "integer", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEmdDriftLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEmdDriftLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLIntegratedGradientsLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    AttributionsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    PredictionScore = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    BaselineScore = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IntegrationSteps = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLIntegratedGradientsLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLIntegratedGradientsLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLJsFeatureRanking",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeatureRankingsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    TopFeatureIndicesJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLJsFeatureRanking", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLJsFeatureRanking_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLLimeExplanationLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    PredictionLogId = table.Column<long>(type: "bigint", nullable: false),
                    LocalCoefficientsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    LocalIntercept = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    LocalR2 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLimeExplanationLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLLimeExplanationLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLMdnParams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    NumComponents = table.Column<int>(type: "integer", nullable: false),
                    PiJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    MuJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    SigmaJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMdnParams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMdnParams_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLMiRedundancyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    Feature1Index = table.Column<int>(type: "integer", nullable: false),
                    Feature2Index = table.Column<int>(type: "integer", nullable: false),
                    MutualInformation = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsRedundant = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMiRedundancyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMiRedundancyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLMoeGatingLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    Expert0Activations = table.Column<int>(type: "integer", nullable: false),
                    Expert1Activations = table.Column<int>(type: "integer", nullable: false),
                    Expert2Activations = table.Column<int>(type: "integer", nullable: false),
                    Expert3Activations = table.Column<int>(type: "integer", nullable: false),
                    TotalSamples = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMoeGatingLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMoeGatingLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLMondrianCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    RegimeName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    QHat = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EmpiricalCoverage = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMondrianCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMondrianCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLNeuralProcessEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    EncoderWeightsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    DecoderWeightsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    LatentDim = table.Column<int>(type: "integer", nullable: false),
                    ContextSize = table.Column<int>(type: "integer", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNeuralProcessEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRegimePrototype",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    RegimeName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PrototypeFeaturesJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRegimePrototype", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRegimePrototype_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLActivationMaxLog_MLModelId",
                table: "MLActivationMaxLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBayesThresholdLog_MLModelId",
                table: "MLBayesThresholdLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLC51Distribution_MLModelId",
                table: "MLC51Distribution",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCashSelectionLog_MLModelId",
                table: "MLCashSelectionLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLContrastiveEncoder_Symbol_Timeframe",
                table: "MLContrastiveEncoder",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCrcCalibration_MLModelId",
                table: "MLCrcCalibration",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEmdDriftLog_MLModelId",
                table: "MLEmdDriftLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLIntegratedGradientsLog_MLModelId",
                table: "MLIntegratedGradientsLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLJsFeatureRanking_MLModelId",
                table: "MLJsFeatureRanking",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLimeExplanationLog_MLModelId",
                table: "MLLimeExplanationLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMdnParams_MLModelId",
                table: "MLMdnParams",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMiRedundancyLog_MLModelId",
                table: "MLMiRedundancyLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMoeGatingLog_MLModelId",
                table: "MLMoeGatingLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMondrianCalibration_MLModelId",
                table: "MLMondrianCalibration",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLNeuralProcessEncoder_Symbol_Timeframe",
                table: "MLNeuralProcessEncoder",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRegimePrototype_MLModelId",
                table: "MLRegimePrototype",
                column: "MLModelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLActivationMaxLog");

            migrationBuilder.DropTable(
                name: "MLBayesThresholdLog");

            migrationBuilder.DropTable(
                name: "MLC51Distribution");

            migrationBuilder.DropTable(
                name: "MLCashSelectionLog");

            migrationBuilder.DropTable(
                name: "MLContrastiveEncoder");

            migrationBuilder.DropTable(
                name: "MLCrcCalibration");

            migrationBuilder.DropTable(
                name: "MLEmdDriftLog");

            migrationBuilder.DropTable(
                name: "MLIntegratedGradientsLog");

            migrationBuilder.DropTable(
                name: "MLJsFeatureRanking");

            migrationBuilder.DropTable(
                name: "MLLimeExplanationLog");

            migrationBuilder.DropTable(
                name: "MLMdnParams");

            migrationBuilder.DropTable(
                name: "MLMiRedundancyLog");

            migrationBuilder.DropTable(
                name: "MLMoeGatingLog");

            migrationBuilder.DropTable(
                name: "MLMondrianCalibration");

            migrationBuilder.DropTable(
                name: "MLNeuralProcessEncoder");

            migrationBuilder.DropTable(
                name: "MLRegimePrototype");
        }
    }
}
