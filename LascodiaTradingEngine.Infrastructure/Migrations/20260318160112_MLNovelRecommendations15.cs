using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations15 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLBaldLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SamplesEvaluated = table.Column<int>(type: "integer", nullable: false),
                    TopUncertaintySample = table.Column<int>(type: "integer", nullable: false),
                    MeanBaldScore = table.Column<double>(type: "double precision", nullable: false),
                    MaxBaldScore = table.Column<double>(type: "double precision", nullable: false),
                    EpistemicEntropy = table.Column<double>(type: "double precision", nullable: false),
                    AleatoricEntropy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBaldLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLBaldLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLBarlowTwinsLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    InvarianceLoss = table.Column<double>(type: "double precision", nullable: false),
                    RedundancyLoss = table.Column<double>(type: "double precision", nullable: false),
                    TotalLoss = table.Column<double>(type: "double precision", nullable: false),
                    CrossCorrelationDiagMean = table.Column<double>(type: "double precision", nullable: false),
                    CrossCorrelationOffDiagMean = table.Column<double>(type: "double precision", nullable: false),
                    LatentDimensions = table.Column<int>(type: "integer", nullable: false),
                    Epochs = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBarlowTwinsLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLBarlowTwinsLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLCandlestickLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DojiCount = table.Column<int>(type: "integer", nullable: false),
                    HammerCount = table.Column<int>(type: "integer", nullable: false),
                    EngulfingCount = table.Column<int>(type: "integer", nullable: false),
                    InsideBarCount = table.Column<int>(type: "integer", nullable: false),
                    ThreeBarReversalCount = table.Column<int>(type: "integer", nullable: false),
                    MostRecentPattern = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CandleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCandlestickLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCqrLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    QuantileLow = table.Column<double>(type: "double precision", nullable: false),
                    QuantileHigh = table.Column<double>(type: "double precision", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    CoverageProbability = table.Column<double>(type: "double precision", nullable: false),
                    MeanIntervalWidth = table.Column<double>(type: "double precision", nullable: false),
                    ConditionalCoverage = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCqrLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCqrLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLFactorModelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MomentumBeta = table.Column<double>(type: "double precision", nullable: false),
                    CarryBeta = table.Column<double>(type: "double precision", nullable: false),
                    VolatilityBeta = table.Column<double>(type: "double precision", nullable: false),
                    AlphaReturn = table.Column<double>(type: "double precision", nullable: false),
                    SystematicReturn = table.Column<double>(type: "double precision", nullable: false),
                    IdiosyncraticReturn = table.Column<double>(type: "double precision", nullable: false),
                    RSquared = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFactorModelLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLFactorModelLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLIcaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    MeanKurtosis = table.Column<double>(type: "double precision", nullable: false),
                    MeanNegentropy = table.Column<double>(type: "double precision", nullable: false),
                    MixingMatrixNorm = table.Column<double>(type: "double precision", nullable: false),
                    ConvergedIterations = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLIcaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLJohansenLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TraceStatistic = table.Column<double>(type: "double precision", nullable: false),
                    CriticalValue95 = table.Column<double>(type: "double precision", nullable: false),
                    MaxEigenStatistic = table.Column<double>(type: "double precision", nullable: false),
                    CointegrationRank = table.Column<int>(type: "integer", nullable: false),
                    IsCointegrated = table.Column<bool>(type: "boolean", nullable: false),
                    Beta1 = table.Column<double>(type: "double precision", nullable: false),
                    Beta2 = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLJohansenLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLJohansenLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLMarchenkoPasturLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    NoiseEigenvalueCount = table.Column<int>(type: "integer", nullable: false),
                    SignalEigenvalueCount = table.Column<int>(type: "integer", nullable: false),
                    LambdaMax = table.Column<double>(type: "double precision", nullable: false),
                    LambdaMin = table.Column<double>(type: "double precision", nullable: false),
                    CleanedConditionNumber = table.Column<double>(type: "double precision", nullable: false),
                    RawConditionNumber = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMarchenkoPasturLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMatrixProfileLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SubsequenceLength = table.Column<int>(type: "integer", nullable: false),
                    MotifDistance = table.Column<double>(type: "double precision", nullable: false),
                    MotifIndexA = table.Column<int>(type: "integer", nullable: false),
                    MotifIndexB = table.Column<int>(type: "integer", nullable: false),
                    DiscordDistance = table.Column<double>(type: "double precision", nullable: false),
                    DiscordIndex = table.Column<int>(type: "integer", nullable: false),
                    MatrixProfileMean = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMatrixProfileLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMatrixProfileLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLMaxEntLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MaxEntropyValue = table.Column<double>(type: "double precision", nullable: false),
                    MomentConstraintError = table.Column<double>(type: "double precision", nullable: false),
                    LagrangeMultiplierNorm = table.Column<double>(type: "double precision", nullable: false),
                    InformativeFeatureCount = table.Column<int>(type: "integer", nullable: false),
                    SurpriseThreshold = table.Column<double>(type: "double precision", nullable: false),
                    HighSurpriseSamples = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMaxEntLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMaxEntLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLNmfLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", nullable: false),
                    SparsityBasis = table.Column<double>(type: "double precision", nullable: false),
                    SparsityCoeff = table.Column<double>(type: "double precision", nullable: false),
                    ConvergedIterations = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNmfLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNsgaIILogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PopulationSize = table.Column<int>(type: "integer", nullable: false),
                    Generations = table.Column<int>(type: "integer", nullable: false),
                    ParetoFrontSize = table.Column<int>(type: "integer", nullable: false),
                    BestAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    BestEce = table.Column<double>(type: "double precision", nullable: false),
                    BestLatencyMs = table.Column<double>(type: "double precision", nullable: false),
                    HypervolumeDominated = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNsgaIILogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLNsgaIILogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLOfiLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MeanOfi = table.Column<double>(type: "double precision", nullable: false),
                    StdOfi = table.Column<double>(type: "double precision", nullable: false),
                    CurrentOfi = table.Column<double>(type: "double precision", nullable: false),
                    OfiPercentileRank = table.Column<double>(type: "double precision", nullable: false),
                    BullishPressure = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOfiLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLOuHalfLifeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MeanReversionSpeed = table.Column<double>(type: "double precision", nullable: false),
                    LongRunMean = table.Column<double>(type: "double precision", nullable: false),
                    DiffusionCoeff = table.Column<double>(type: "double precision", nullable: false),
                    HalfLifeDays = table.Column<double>(type: "double precision", nullable: false),
                    AdfStatistic = table.Column<double>(type: "double precision", nullable: false),
                    OuFitResidual = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOuHalfLifeLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLOuHalfLifeLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLRademacherLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Complexity = table.Column<double>(type: "double precision", nullable: false),
                    GeneralizationBound = table.Column<double>(type: "double precision", nullable: false),
                    TrainAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    BoundSlack = table.Column<double>(type: "double precision", nullable: false),
                    RandomTrials = table.Column<int>(type: "integer", nullable: false),
                    OverfittingRisk = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRademacherLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRademacherLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLSinkhornLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RegularizationEps = table.Column<double>(type: "double precision", nullable: false),
                    OtDistance = table.Column<double>(type: "double precision", nullable: false),
                    TransportCost = table.Column<double>(type: "double precision", nullable: false),
                    SinkhornIterations = table.Column<int>(type: "integer", nullable: false),
                    MarginalError = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSinkhornLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSinkhornLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLSparsePcaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    SparsityL1 = table.Column<double>(type: "double precision", nullable: false),
                    MeanNonZeroLoadings = table.Column<double>(type: "double precision", nullable: false),
                    ExplainedVarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSparsePcaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSplLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CurrentPace = table.Column<double>(type: "double precision", nullable: false),
                    SamplesSelected = table.Column<int>(type: "integer", nullable: false),
                    TotalSamples = table.Column<int>(type: "integer", nullable: false),
                    MeanSelectedLoss = table.Column<double>(type: "double precision", nullable: false),
                    MeanRejectedLoss = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyGain = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSplLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSplLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLStatArbLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SpreadMean = table.Column<double>(type: "double precision", nullable: false),
                    SpreadStd = table.Column<double>(type: "double precision", nullable: false),
                    CurrentZScore = table.Column<double>(type: "double precision", nullable: false),
                    HalfLifeDays = table.Column<double>(type: "double precision", nullable: false),
                    EntryThreshold = table.Column<double>(type: "double precision", nullable: false),
                    SignalActive = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStatArbLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLStatArbLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLStlDecompositionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TrendMean = table.Column<double>(type: "double precision", nullable: false),
                    TrendSlope = table.Column<double>(type: "double precision", nullable: false),
                    SeasonalAmplitude = table.Column<double>(type: "double precision", nullable: false),
                    ResidualVariance = table.Column<double>(type: "double precision", nullable: false),
                    SeasonalPeriod = table.Column<int>(type: "integer", nullable: false),
                    CandleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStlDecompositionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLStlDecompositionLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLBaldLogs_MLModelId",
                table: "MLBaldLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBarlowTwinsLogs_MLModelId",
                table: "MLBarlowTwinsLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCandlestickLogs_Symbol",
                table: "MLCandlestickLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLCqrLogs_MLModelId",
                table: "MLCqrLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLFactorModelLogs_MLModelId",
                table: "MLFactorModelLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLIcaLogs_Symbol",
                table: "MLIcaLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLJohansenLogs_MLModelId",
                table: "MLJohansenLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMarchenkoPasturLogs_Symbol",
                table: "MLMarchenkoPasturLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLMatrixProfileLogs_MLModelId",
                table: "MLMatrixProfileLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMaxEntLogs_MLModelId",
                table: "MLMaxEntLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLNmfLogs_Symbol",
                table: "MLNmfLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLNsgaIILogs_MLModelId",
                table: "MLNsgaIILogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOfiLogs_Symbol",
                table: "MLOfiLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLOuHalfLifeLogs_MLModelId",
                table: "MLOuHalfLifeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRademacherLogs_MLModelId",
                table: "MLRademacherLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSinkhornLogs_MLModelId",
                table: "MLSinkhornLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSparsePcaLogs_Symbol",
                table: "MLSparsePcaLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLSplLogs_MLModelId",
                table: "MLSplLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLStatArbLogs_MLModelId",
                table: "MLStatArbLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLStlDecompositionLogs_MLModelId",
                table: "MLStlDecompositionLogs",
                column: "MLModelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLBaldLogs");

            migrationBuilder.DropTable(
                name: "MLBarlowTwinsLogs");

            migrationBuilder.DropTable(
                name: "MLCandlestickLogs");

            migrationBuilder.DropTable(
                name: "MLCqrLogs");

            migrationBuilder.DropTable(
                name: "MLFactorModelLogs");

            migrationBuilder.DropTable(
                name: "MLIcaLogs");

            migrationBuilder.DropTable(
                name: "MLJohansenLogs");

            migrationBuilder.DropTable(
                name: "MLMarchenkoPasturLogs");

            migrationBuilder.DropTable(
                name: "MLMatrixProfileLogs");

            migrationBuilder.DropTable(
                name: "MLMaxEntLogs");

            migrationBuilder.DropTable(
                name: "MLNmfLogs");

            migrationBuilder.DropTable(
                name: "MLNsgaIILogs");

            migrationBuilder.DropTable(
                name: "MLOfiLogs");

            migrationBuilder.DropTable(
                name: "MLOuHalfLifeLogs");

            migrationBuilder.DropTable(
                name: "MLRademacherLogs");

            migrationBuilder.DropTable(
                name: "MLSinkhornLogs");

            migrationBuilder.DropTable(
                name: "MLSparsePcaLogs");

            migrationBuilder.DropTable(
                name: "MLSplLogs");

            migrationBuilder.DropTable(
                name: "MLStatArbLogs");

            migrationBuilder.DropTable(
                name: "MLStlDecompositionLogs");
        }
    }
}
