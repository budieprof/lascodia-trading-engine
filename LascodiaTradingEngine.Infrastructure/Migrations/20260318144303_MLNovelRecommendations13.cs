using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations13 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLA2cLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Episodes = table.Column<int>(type: "integer", nullable: false),
                    MeanReturn = table.Column<double>(type: "double precision", nullable: false),
                    ActorEntropyMean = table.Column<double>(type: "double precision", nullable: false),
                    CriticLossMean = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLA2cLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAleLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AleValuesJson = table.Column<string>(type: "text", nullable: false),
                    MaxAbsEffect = table.Column<double>(type: "double precision", nullable: false),
                    MeanAbsEffect = table.Column<double>(type: "double precision", nullable: false),
                    QuantileCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAleEffect = table.Column<double>(type: "double precision", nullable: false),
                    MinAleEffect = table.Column<double>(type: "double precision", nullable: false),
                    MeanAbsAleEffect = table.Column<double>(type: "double precision", nullable: false),
                    GridPoints = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAleLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLConsistencyModelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NoiseLevels = table.Column<int>(type: "integer", nullable: false),
                    MeanConsistencyLoss = table.Column<double>(type: "double precision", nullable: false),
                    BestNoiseLevel = table.Column<double>(type: "double precision", nullable: false),
                    ConsistencyImprovement = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConsistencyModelLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCounterfactualLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SamplesAnalyzed = table.Column<int>(type: "integer", nullable: false),
                    MeanFeaturesChanged = table.Column<double>(type: "double precision", nullable: false),
                    MinFeaturesChanged = table.Column<int>(type: "integer", nullable: false),
                    SuccessRate = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCounterfactualLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCrossStrategyTransferLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DonorModelId = table.Column<long>(type: "bigint", nullable: false),
                    DonorTimeframe = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    WeightSimilarity = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyGap = table.Column<double>(type: "double precision", nullable: false),
                    TransferRecommended = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCrossStrategyTransferLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDqnLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Episodes = table.Column<int>(type: "integer", nullable: false),
                    MeanReward = table.Column<double>(type: "double precision", nullable: false),
                    EpsilonFinal = table.Column<double>(type: "double precision", nullable: false),
                    BestAction = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDqnLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEvalueLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    CurrentEValue = table.Column<double>(type: "double precision", nullable: false),
                    SignificanceThreshold = table.Column<double>(type: "double precision", nullable: false),
                    SignificanceReached = table.Column<bool>(type: "boolean", nullable: false),
                    LogEValue = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEvalueLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFlowMatchingLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrainingLoss = table.Column<double>(type: "double precision", nullable: false),
                    MmdSyntheticVsReal = table.Column<double>(type: "double precision", nullable: false),
                    TrainingEpochs = table.Column<int>(type: "integer", nullable: false),
                    QualityAcceptable = table.Column<bool>(type: "boolean", nullable: false),
                    MeanFeatureDelta = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    MeanVelocityMagnitude = table.Column<double>(type: "double precision", nullable: false),
                    VelocityCurvature = table.Column<double>(type: "double precision", nullable: false),
                    AugmentedSamplesGenerated = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFlowMatchingLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGasModelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TimeVaryingThreshold = table.Column<double>(type: "double precision", nullable: false),
                    ScoreGradient = table.Column<double>(type: "double precision", nullable: false),
                    OmegaParam = table.Column<double>(type: "double precision", nullable: false),
                    AlphaParam = table.Column<double>(type: "double precision", nullable: false),
                    BetaParam = table.Column<double>(type: "double precision", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    Omega = table.Column<double>(type: "double precision", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    Beta = table.Column<double>(type: "double precision", nullable: false),
                    ForecastedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ParameterStability = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGasModelLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGemLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EpisodeSize = table.Column<int>(type: "integer", nullable: false),
                    GradientConflicts = table.Column<int>(type: "integer", nullable: false),
                    MeanGradientCosine = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGemLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLInformationShareLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InformationShare = table.Column<double>(type: "double precision", nullable: false),
                    CommonFactor = table.Column<double>(type: "double precision", nullable: false),
                    IdiosyncraticVariance = table.Column<double>(type: "double precision", nullable: false),
                    IsDominantSource = table.Column<bool>(type: "boolean", nullable: false),
                    ComparedToSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SignalVariance = table.Column<double>(type: "double precision", nullable: false),
                    ReturnVariance = table.Column<double>(type: "double precision", nullable: false),
                    CrossCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLInformationShareLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLearnThenTestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    CalibratedThreshold = table.Column<double>(type: "double precision", nullable: false),
                    EmpiricalRisk = table.Column<double>(type: "double precision", nullable: false),
                    CoverageGuarantee = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLearnThenTestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLedoitWolfLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    ShrinkageCoeff = table.Column<double>(type: "double precision", nullable: false),
                    ConditionNumber = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLedoitWolfLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLowRankLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", nullable: false),
                    OriginalAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    LowRankAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyRetention = table.Column<double>(type: "double precision", nullable: false),
                    OriginalParamCount = table.Column<int>(type: "integer", nullable: false),
                    LowRankParamCount = table.Column<int>(type: "integer", nullable: false),
                    CompressionRatio = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLowRankLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMcdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DropoutRate = table.Column<double>(type: "double precision", nullable: false),
                    ForwardPasses = table.Column<int>(type: "integer", nullable: false),
                    MeanPrediction = table.Column<double>(type: "double precision", nullable: false),
                    PredictionStd = table.Column<double>(type: "double precision", nullable: false),
                    HighUncertaintyCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMcdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNflTestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ObservedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    NullMeanAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    NullStdAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    EmpiricalPValue = table.Column<double>(type: "double precision", nullable: false),
                    MonteCarloRuns = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNflTestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNode2VecEmbeddingLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EmbeddingJson = table.Column<string>(type: "text", nullable: false),
                    NearestNeighborDistance = table.Column<double>(type: "double precision", nullable: false),
                    MostSimilarSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WalkCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNode2VecEmbeddingLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNode2VecLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    TopFeature1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TopFeature2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TopFeature3 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MeanCentrality = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNode2VecLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPackNetLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MaskCount = table.Column<int>(type: "integer", nullable: false),
                    Sparsity = table.Column<double>(type: "double precision", nullable: false),
                    MeanMaskedWeight = table.Column<double>(type: "double precision", nullable: false),
                    MeanUnmaskedWeight = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPackNetLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLParticleFilterLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PosteriorRegime0 = table.Column<double>(type: "double precision", nullable: false),
                    PosteriorRegime1 = table.Column<double>(type: "double precision", nullable: false),
                    EffectiveSampleSize = table.Column<double>(type: "double precision", nullable: false),
                    ParticleCount = table.Column<int>(type: "integer", nullable: false),
                    ResamplingTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    PosteriorMeanAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    PosteriorStd = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLParticleFilterLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLQuantizationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BitsSimulated = table.Column<int>(type: "integer", nullable: false),
                    MeanAbsError = table.Column<double>(type: "double precision", nullable: false),
                    MaxAbsError = table.Column<double>(type: "double precision", nullable: false),
                    QuantizationRange = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLQuantizationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRealizedKernelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RealizedKernelVol = table.Column<double>(type: "double precision", nullable: false),
                    RealizedVariance = table.Column<double>(type: "double precision", nullable: false),
                    BandwidthH = table.Column<int>(type: "integer", nullable: false),
                    KernelWeight = table.Column<double>(type: "double precision", nullable: false),
                    MicrostructureNoiseEstimate = table.Column<double>(type: "double precision", nullable: false),
                    KernelBandwidth = table.Column<int>(type: "integer", nullable: false),
                    RealizedVol = table.Column<double>(type: "double precision", nullable: false),
                    MicrostructureNoise = table.Column<double>(type: "double precision", nullable: false),
                    CandleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRealizedKernelLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRealNvpLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    MeanLogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    StdLogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    AnomalyCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRealNvpLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLReptileLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    InnerSteps = table.Column<int>(type: "integer", nullable: false),
                    WeightDisplacementNorm = table.Column<double>(type: "double precision", nullable: false),
                    MetaLearningRate = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLReptileLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSacLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdateSteps = table.Column<int>(type: "integer", nullable: false),
                    MeanQValue = table.Column<double>(type: "double precision", nullable: false),
                    PolicyEntropy = table.Column<double>(type: "double precision", nullable: false),
                    EntropyCoeff = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSacLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSgldLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SamplingSteps = table.Column<int>(type: "integer", nullable: false),
                    MeanWeightVariance = table.Column<double>(type: "double precision", nullable: false),
                    NoiseScale = table.Column<double>(type: "double precision", nullable: false),
                    PosteriorMeanShift = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSgldLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSpectralGraphLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Cluster1Size = table.Column<int>(type: "integer", nullable: false),
                    Cluster2Size = table.Column<int>(type: "integer", nullable: false),
                    FiedlerValue = table.Column<double>(type: "double precision", nullable: false),
                    SpectralGap = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSpectralGraphLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLStructuredPruningLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PrunedNeurons = table.Column<int>(type: "integer", nullable: false),
                    TotalNeurons = table.Column<int>(type: "integer", nullable: false),
                    PruningRatio = table.Column<double>(type: "double precision", nullable: false),
                    MinRowNorm = table.Column<double>(type: "double precision", nullable: false),
                    MaxRowNorm = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStructuredPruningLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSurvivalLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CoxCoefficientsJson = table.Column<string>(type: "text", nullable: false),
                    BaselineHazardMean = table.Column<double>(type: "double precision", nullable: false),
                    ConcordanceIndex = table.Column<double>(type: "double precision", nullable: false),
                    MedianSurvivalTime = table.Column<double>(type: "double precision", nullable: false),
                    HazardAtCurrentFeatures = table.Column<double>(type: "double precision", nullable: false),
                    MedianSurvivalDays = table.Column<double>(type: "double precision", nullable: false),
                    Survival30Days = table.Column<double>(type: "double precision", nullable: false),
                    Survival60Days = table.Column<double>(type: "double precision", nullable: false),
                    Survival90Days = table.Column<double>(type: "double precision", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSurvivalLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSyntheticControlLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    TreatmentSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ControlWeightsJson = table.Column<string>(type: "text", nullable: false),
                    PreTreatmentFit = table.Column<double>(type: "double precision", nullable: false),
                    PostTreatmentEffect = table.Column<double>(type: "double precision", nullable: false),
                    SignificantEffect = table.Column<bool>(type: "boolean", nullable: false),
                    DonorCount = table.Column<int>(type: "integer", nullable: false),
                    PrePeriodFit = table.Column<double>(type: "double precision", nullable: false),
                    TreatmentEffect = table.Column<double>(type: "double precision", nullable: false),
                    PostPeriodActual = table.Column<double>(type: "double precision", nullable: false),
                    PostPeriodSynthetic = table.Column<double>(type: "double precision", nullable: false),
                    EventDescription = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EventDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSyntheticControlLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLWeibullTteLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WeibullShape = table.Column<double>(type: "double precision", nullable: false),
                    WeibullScale = table.Column<double>(type: "double precision", nullable: false),
                    MeanRecoveryDays = table.Column<double>(type: "double precision", nullable: false),
                    Percentile90RecoveryDays = table.Column<double>(type: "double precision", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLWeibullTteLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLA2cLogs_MLModelId",
                table: "MLA2cLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLAleLogs_MLModelId",
                table: "MLAleLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLConsistencyModelLogs_MLModelId",
                table: "MLConsistencyModelLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCounterfactualLogs_MLModelId",
                table: "MLCounterfactualLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrossStrategyTransferLogs_MLModelId",
                table: "MLCrossStrategyTransferLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDqnLogs_MLModelId",
                table: "MLDqnLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEvalueLogs_MLModelId",
                table: "MLEvalueLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLFlowMatchingLogs_MLModelId",
                table: "MLFlowMatchingLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGasModelLogs_MLModelId",
                table: "MLGasModelLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGemLogs_MLModelId",
                table: "MLGemLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLInformationShareLogs_MLModelId",
                table: "MLInformationShareLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLearnThenTestLogs_MLModelId",
                table: "MLLearnThenTestLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLedoitWolfLogs_MLModelId",
                table: "MLLedoitWolfLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLowRankLogs_MLModelId",
                table: "MLLowRankLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMcdLogs_MLModelId",
                table: "MLMcdLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLNflTestLogs_MLModelId",
                table: "MLNflTestLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLNode2VecEmbeddingLogs_Symbol",
                table: "MLNode2VecEmbeddingLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLNode2VecLogs_MLModelId",
                table: "MLNode2VecLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPackNetLogs_MLModelId",
                table: "MLPackNetLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLParticleFilterLogs_MLModelId",
                table: "MLParticleFilterLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLQuantizationLogs_MLModelId",
                table: "MLQuantizationLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRealizedKernelLogs_MLModelId",
                table: "MLRealizedKernelLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRealNvpLogs_MLModelId",
                table: "MLRealNvpLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLReptileLogs_MLModelId",
                table: "MLReptileLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSacLogs_MLModelId",
                table: "MLSacLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSgldLogs_MLModelId",
                table: "MLSgldLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSpectralGraphLogs_MLModelId",
                table: "MLSpectralGraphLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLStructuredPruningLogs_MLModelId",
                table: "MLStructuredPruningLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSurvivalLogs_MLModelId",
                table: "MLSurvivalLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSyntheticControlLogs_MLModelId",
                table: "MLSyntheticControlLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLWeibullTteLogs_MLModelId",
                table: "MLWeibullTteLogs",
                column: "MLModelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLA2cLogs");

            migrationBuilder.DropTable(
                name: "MLAleLogs");

            migrationBuilder.DropTable(
                name: "MLConsistencyModelLogs");

            migrationBuilder.DropTable(
                name: "MLCounterfactualLogs");

            migrationBuilder.DropTable(
                name: "MLCrossStrategyTransferLogs");

            migrationBuilder.DropTable(
                name: "MLDqnLogs");

            migrationBuilder.DropTable(
                name: "MLEvalueLogs");

            migrationBuilder.DropTable(
                name: "MLFlowMatchingLogs");

            migrationBuilder.DropTable(
                name: "MLGasModelLogs");

            migrationBuilder.DropTable(
                name: "MLGemLogs");

            migrationBuilder.DropTable(
                name: "MLInformationShareLogs");

            migrationBuilder.DropTable(
                name: "MLLearnThenTestLogs");

            migrationBuilder.DropTable(
                name: "MLLedoitWolfLogs");

            migrationBuilder.DropTable(
                name: "MLLowRankLogs");

            migrationBuilder.DropTable(
                name: "MLMcdLogs");

            migrationBuilder.DropTable(
                name: "MLNflTestLogs");

            migrationBuilder.DropTable(
                name: "MLNode2VecEmbeddingLogs");

            migrationBuilder.DropTable(
                name: "MLNode2VecLogs");

            migrationBuilder.DropTable(
                name: "MLPackNetLogs");

            migrationBuilder.DropTable(
                name: "MLParticleFilterLogs");

            migrationBuilder.DropTable(
                name: "MLQuantizationLogs");

            migrationBuilder.DropTable(
                name: "MLRealizedKernelLogs");

            migrationBuilder.DropTable(
                name: "MLRealNvpLogs");

            migrationBuilder.DropTable(
                name: "MLReptileLogs");

            migrationBuilder.DropTable(
                name: "MLSacLogs");

            migrationBuilder.DropTable(
                name: "MLSgldLogs");

            migrationBuilder.DropTable(
                name: "MLSpectralGraphLogs");

            migrationBuilder.DropTable(
                name: "MLStructuredPruningLogs");

            migrationBuilder.DropTable(
                name: "MLSurvivalLogs");

            migrationBuilder.DropTable(
                name: "MLSyntheticControlLogs");

            migrationBuilder.DropTable(
                name: "MLWeibullTteLogs");
        }
    }
}
