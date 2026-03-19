using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations16 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLCausalImpactLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EventDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AbsoluteEffect = table.Column<double>(type: "double precision", nullable: false),
                    RelativeEffect = table.Column<double>(type: "double precision", nullable: false),
                    ProbabilityOfCausalEffect = table.Column<double>(type: "double precision", nullable: false),
                    CounterfactualMean = table.Column<double>(type: "double precision", nullable: false),
                    ActualMean = table.Column<double>(type: "double precision", nullable: false),
                    CumulativeEffect = table.Column<double>(type: "double precision", nullable: false),
                    SignificantImpact = table.Column<bool>(type: "boolean", nullable: false),
                    PrePeriodBars = table.Column<int>(type: "integer", nullable: false),
                    PostPeriodBars = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCausalImpactLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCrownLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CertifiedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    MinFlipEpsilon = table.Column<double>(type: "double precision", nullable: false),
                    MeanLipschitzBound = table.Column<double>(type: "double precision", nullable: false),
                    IbpLowerBound = table.Column<double>(type: "double precision", nullable: false),
                    CrownTightnessRatio = table.Column<double>(type: "double precision", nullable: false),
                    PerturbationThreshold = table.Column<double>(type: "double precision", nullable: false),
                    CertifiedCount = table.Column<int>(type: "integer", nullable: false),
                    TotalTestSamples = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCrownLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCrownLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLEnbPiLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CoverageH1 = table.Column<double>(type: "double precision", nullable: false),
                    CoverageH3 = table.Column<double>(type: "double precision", nullable: false),
                    CoverageH5 = table.Column<double>(type: "double precision", nullable: false),
                    MeanIntervalWidthH1 = table.Column<double>(type: "double precision", nullable: false),
                    MeanIntervalWidthH3 = table.Column<double>(type: "double precision", nullable: false),
                    MeanIntervalWidthH5 = table.Column<double>(type: "double precision", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationSetSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEnbPiLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEnbPiLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLExp3Logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ArmCount = table.Column<int>(type: "integer", nullable: false),
                    ArmWeightsJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    SelectedArm = table.Column<int>(type: "integer", nullable: false),
                    SelectedArmProbability = table.Column<double>(type: "double precision", nullable: false),
                    CumulativeRegret = table.Column<double>(type: "double precision", nullable: false),
                    MixingCoeff = table.Column<double>(type: "double precision", nullable: false),
                    LearningRate = table.Column<double>(type: "double precision", nullable: false),
                    TotalRounds = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLExp3Logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLExp4Logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ArmCount = table.Column<int>(type: "integer", nullable: false),
                    ExpertCount = table.Column<int>(type: "integer", nullable: false),
                    ArmWeightsJson = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SelectedArm = table.Column<int>(type: "integer", nullable: false),
                    EstimatedReward = table.Column<double>(type: "double precision", nullable: false),
                    CumulativeRegret = table.Column<double>(type: "double precision", nullable: false),
                    ExpertAgreementRate = table.Column<double>(type: "double precision", nullable: false),
                    TotalRounds = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLExp4Logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFpcaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FirstFpcVarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    SecondFpcVarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    CumulativeVarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    Fpc1CoefficientsJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    MeanFunctionMean = table.Column<double>(type: "double precision", nullable: false),
                    CrossingRate = table.Column<double>(type: "double precision", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    BasisDimension = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFpcaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHjbLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OptimalPositionSize = table.Column<double>(type: "double precision", nullable: false),
                    ExpectedUtility = table.Column<double>(type: "double precision", nullable: false),
                    MertonFraction = table.Column<double>(type: "double precision", nullable: false),
                    RiskAversionGamma = table.Column<double>(type: "double precision", nullable: false),
                    EstimatedDrift = table.Column<double>(type: "double precision", nullable: false),
                    EstimatedVolatility = table.Column<double>(type: "double precision", nullable: false),
                    TransactionCostPenalty = table.Column<double>(type: "double precision", nullable: false),
                    PdeResidualSteps = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHjbLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLHjbLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLHsicLingamLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CausalEdgesJson = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MaxHsicStatistic = table.Column<double>(type: "double precision", nullable: false),
                    MeanHsicStatistic = table.Column<double>(type: "double precision", nullable: false),
                    LinearEdgeCount = table.Column<int>(type: "integer", nullable: false),
                    NonlinearEdgeCount = table.Column<int>(type: "integer", nullable: false),
                    TotalEdgeCount = table.Column<int>(type: "integer", nullable: false),
                    HsicKernelBandwidth = table.Column<double>(type: "double precision", nullable: false),
                    FullyLinearDag = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHsicLingamLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHyperbolicLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MeanHyperbolicDistance = table.Column<double>(type: "double precision", nullable: false),
                    HierarchyScore = table.Column<double>(type: "double precision", nullable: false),
                    EmbeddingLoss = table.Column<double>(type: "double precision", nullable: false),
                    EmbeddingVectorJson = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    DistanceToOrigin = table.Column<double>(type: "double precision", nullable: false),
                    IsDominantNode = table.Column<bool>(type: "boolean", nullable: false),
                    EmbeddingDimension = table.Column<int>(type: "integer", nullable: false),
                    TrainingEpochs = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHyperbolicLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLaplaceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MarginalLogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    PriorPrecision = table.Column<double>(type: "double precision", nullable: false),
                    PosteriorVarianceMean = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationEce = table.Column<double>(type: "double precision", nullable: false),
                    NllImprovement = table.Column<double>(type: "double precision", nullable: false),
                    LastLayerDim = table.Column<int>(type: "integer", nullable: false),
                    HessianApproximation = table.Column<int>(type: "integer", nullable: false),
                    UseLinearizedLaplace = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLaplaceLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLLaplaceLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLLingamLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CausalOrderJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    ConnectivityMatrixNorm = table.Column<double>(type: "double precision", nullable: false),
                    MaxCausalStrength = table.Column<double>(type: "double precision", nullable: false),
                    MeanResidualNonGaussianity = table.Column<double>(type: "double precision", nullable: false),
                    VariableCount = table.Column<int>(type: "integer", nullable: false),
                    ConvergedSuccessfully = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLingamLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMfboLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BestValidationAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    BestHyperparamsJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    FidelityUsed = table.Column<double>(type: "double precision", nullable: false),
                    TotalCostSaved = table.Column<double>(type: "double precision", nullable: false),
                    LowFidelityEvals = table.Column<int>(type: "integer", nullable: false),
                    HighFidelityEvals = table.Column<int>(type: "integer", nullable: false),
                    SurrogateCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    TotalTrials = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMfboLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMfboLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLNcsnLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ScoreMatchingLoss = table.Column<double>(type: "double precision", nullable: false),
                    FidProxy = table.Column<double>(type: "double precision", nullable: false),
                    SampleDiversity = table.Column<double>(type: "double precision", nullable: false),
                    SamplesGenerated = table.Column<int>(type: "integer", nullable: false),
                    NoiseScaleLevels = table.Column<int>(type: "integer", nullable: false),
                    LangevinSteps = table.Column<int>(type: "integer", nullable: false),
                    MaxNoiseLevel = table.Column<double>(type: "double precision", nullable: false),
                    MinNoiseLevel = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNcsnLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSchrodingerBridgeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ForwardKl = table.Column<double>(type: "double precision", nullable: false),
                    ReverseKl = table.Column<double>(type: "double precision", nullable: false),
                    TransportCost = table.Column<double>(type: "double precision", nullable: false),
                    BridgeEntropy = table.Column<double>(type: "double precision", nullable: false),
                    IpfIterations = table.Column<int>(type: "integer", nullable: false),
                    SourceSamples = table.Column<double>(type: "double precision", nullable: false),
                    TargetSamples = table.Column<double>(type: "double precision", nullable: false),
                    ConvergedSuccessfully = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSchrodingerBridgeLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSchrodingerBridgeLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLTdaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BettiZero = table.Column<double>(type: "double precision", nullable: false),
                    BettiOne = table.Column<double>(type: "double precision", nullable: false),
                    PersistenceEntropy = table.Column<double>(type: "double precision", nullable: false),
                    MaxPersistence = table.Column<double>(type: "double precision", nullable: false),
                    MeanPersistence = table.Column<double>(type: "double precision", nullable: false),
                    TopologicalComplexity = table.Column<double>(type: "double precision", nullable: false),
                    PointCloudSize = table.Column<int>(type: "integer", nullable: false),
                    FilteredRadius = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTdaLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLTdaLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLTgcnLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NodeEmbeddingNorm = table.Column<double>(type: "double precision", nullable: false),
                    GraphDensity = table.Column<double>(type: "double precision", nullable: false),
                    MeanEdgeWeight = table.Column<double>(type: "double precision", nullable: false),
                    TemporalSmoothness = table.Column<double>(type: "double precision", nullable: false),
                    TopNeighboursJson = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NodeCount = table.Column<int>(type: "integer", nullable: false),
                    GcnLayers = table.Column<int>(type: "integer", nullable: false),
                    GruHiddenDim = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTgcnLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTuckerLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", nullable: false),
                    CoreTensorNorm = table.Column<double>(type: "double precision", nullable: false),
                    RelativeError = table.Column<double>(type: "double precision", nullable: false),
                    RankAsset = table.Column<int>(type: "integer", nullable: false),
                    RankFeature = table.Column<int>(type: "integer", nullable: false),
                    RankTimeframe = table.Column<int>(type: "integer", nullable: false),
                    AssetCount = table.Column<int>(type: "integer", nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    TimeframeCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTuckerLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLCausalImpactLogs_ComputedAt",
                table: "MLCausalImpactLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLCausalImpactLogs_Symbol_Timeframe_EventDate",
                table: "MLCausalImpactLogs",
                columns: new[] { "Symbol", "Timeframe", "EventDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCrownLogs_MLModelId",
                table: "MLCrownLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrownLogs_Symbol_Timeframe",
                table: "MLCrownLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEnbPiLogs_MLModelId",
                table: "MLEnbPiLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLExp3Logs_Symbol",
                table: "MLExp3Logs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLExp4Logs_ComputedAt",
                table: "MLExp4Logs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLExp4Logs_Symbol_Timeframe",
                table: "MLExp4Logs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFpcaLogs_Symbol",
                table: "MLFpcaLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLHjbLogs_MLModelId",
                table: "MLHjbLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHsicLingamLogs_ComputedAt",
                table: "MLHsicLingamLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLHsicLingamLogs_Symbol_Timeframe",
                table: "MLHsicLingamLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLHyperbolicLogs_Symbol",
                table: "MLHyperbolicLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLLaplaceLogs_MLModelId",
                table: "MLLaplaceLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLaplaceLogs_Symbol_Timeframe",
                table: "MLLaplaceLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLingamLogs_Symbol",
                table: "MLLingamLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLMfboLogs_MLModelId",
                table: "MLMfboLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLNcsnLogs_ComputedAt",
                table: "MLNcsnLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLNcsnLogs_Symbol_Timeframe",
                table: "MLNcsnLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSchrodingerBridgeLogs_MLModelId",
                table: "MLSchrodingerBridgeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSchrodingerBridgeLogs_Symbol_Timeframe",
                table: "MLSchrodingerBridgeLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLTdaLogs_MLModelId",
                table: "MLTdaLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTgcnLogs_ComputedAt",
                table: "MLTgcnLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLTgcnLogs_Symbol_Timeframe",
                table: "MLTgcnLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLTuckerLogs_Symbol",
                table: "MLTuckerLogs",
                column: "Symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLCausalImpactLogs");

            migrationBuilder.DropTable(
                name: "MLCrownLogs");

            migrationBuilder.DropTable(
                name: "MLEnbPiLogs");

            migrationBuilder.DropTable(
                name: "MLExp3Logs");

            migrationBuilder.DropTable(
                name: "MLExp4Logs");

            migrationBuilder.DropTable(
                name: "MLFpcaLogs");

            migrationBuilder.DropTable(
                name: "MLHjbLogs");

            migrationBuilder.DropTable(
                name: "MLHsicLingamLogs");

            migrationBuilder.DropTable(
                name: "MLHyperbolicLogs");

            migrationBuilder.DropTable(
                name: "MLLaplaceLogs");

            migrationBuilder.DropTable(
                name: "MLLingamLogs");

            migrationBuilder.DropTable(
                name: "MLMfboLogs");

            migrationBuilder.DropTable(
                name: "MLNcsnLogs");

            migrationBuilder.DropTable(
                name: "MLSchrodingerBridgeLogs");

            migrationBuilder.DropTable(
                name: "MLTdaLogs");

            migrationBuilder.DropTable(
                name: "MLTgcnLogs");

            migrationBuilder.DropTable(
                name: "MLTuckerLogs");
        }
    }
}
