using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations14 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_MLRollSpreadLog",
                table: "MLRollSpreadLog");

            migrationBuilder.DropIndex(
                name: "IX_MLRollSpreadLog_Symbol_Timeframe",
                table: "MLRollSpreadLog");

            migrationBuilder.RenameTable(
                name: "MLRollSpreadLog",
                newName: "MLRollSpreadLogs");

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "MLRollSpreadLogs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<double>(
                name: "EffectiveBidAskBps",
                table: "MLRollSpreadLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ReturnAutocovariance",
                table: "MLRollSpreadLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "RollSpreadEstimate",
                table: "MLRollSpreadLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "SampleSize",
                table: "MLRollSpreadLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SpreadAlert",
                table: "MLRollSpreadLogs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddPrimaryKey(
                name: "PK_MLRollSpreadLogs",
                table: "MLRollSpreadLogs",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "MLAbsorptionRatioLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    KComponents = table.Column<int>(type: "integer", nullable: false),
                    AbsorptionRatio = table.Column<double>(type: "double precision", nullable: false),
                    PreviousAbsorptionRatio = table.Column<double>(type: "double precision", nullable: false),
                    RatioChange = table.Column<double>(type: "double precision", nullable: false),
                    SystemicRiskHigh = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAbsorptionRatioLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAdwinLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CurrentWindowSize = table.Column<int>(type: "integer", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    DriftCount = table.Column<int>(type: "integer", nullable: false),
                    MeanAccuracyOld = table.Column<double>(type: "double precision", nullable: false),
                    MeanAccuracyNew = table.Column<double>(type: "double precision", nullable: false),
                    DeltaParam = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdwinLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAmihudLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    MeanIlliquidity = table.Column<double>(type: "double precision", nullable: false),
                    StdIlliquidity = table.Column<double>(type: "double precision", nullable: false),
                    CurrentIlliquidity = table.Column<double>(type: "double precision", nullable: false),
                    PercentileRank = table.Column<double>(type: "double precision", nullable: false),
                    IlliquidityAlert = table.Column<bool>(type: "boolean", nullable: false),
                    SampleDays = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAmihudLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBetaVaeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    BetaValue = table.Column<double>(type: "double precision", nullable: false),
                    ReconstructionLoss = table.Column<double>(type: "double precision", nullable: false),
                    KlDivergence = table.Column<double>(type: "double precision", nullable: false),
                    DisentanglementScore = table.Column<double>(type: "double precision", nullable: false),
                    LatentDimensions = table.Column<int>(type: "integer", nullable: false),
                    MeanLatentVariance = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBetaVaeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBlackLittermanLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ViewReturn = table.Column<double>(type: "double precision", nullable: false),
                    ViewConfidence = table.Column<double>(type: "double precision", nullable: false),
                    BlendedReturn = table.Column<double>(type: "double precision", nullable: false),
                    EquilibriumReturn = table.Column<double>(type: "double precision", nullable: false),
                    OptimalWeight = table.Column<double>(type: "double precision", nullable: false),
                    TauParam = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBlackLittermanLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBocpdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RunLengthMode = table.Column<int>(type: "integer", nullable: false),
                    ChangePointProbability = table.Column<double>(type: "double precision", nullable: false),
                    PriorHazardRate = table.Column<double>(type: "double precision", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    CredibleIntervalLow = table.Column<int>(type: "integer", nullable: false),
                    CredibleIntervalHigh = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBocpdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDesLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    NeighborhoodSize = table.Column<int>(type: "integer", nullable: false),
                    SelectedLearners = table.Column<int>(type: "integer", nullable: false),
                    TotalLearners = table.Column<int>(type: "integer", nullable: false),
                    LocalAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    GlobalAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    CompetenceThreshold = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDesLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDklLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    KernelLengthScale = table.Column<double>(type: "double precision", nullable: false),
                    KernelOutputScale = table.Column<double>(type: "double precision", nullable: false),
                    MeanPrediction = table.Column<double>(type: "double precision", nullable: false),
                    VarianceMean = table.Column<double>(type: "double precision", nullable: false),
                    NllScore = table.Column<double>(type: "double precision", nullable: false),
                    InducingPoints = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDklLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEbmLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    MeanEnergyReal = table.Column<double>(type: "double precision", nullable: false),
                    MeanEnergyFantasy = table.Column<double>(type: "double precision", nullable: false),
                    EnergyGap = table.Column<double>(type: "double precision", nullable: false),
                    ContrastiveDivergence = table.Column<double>(type: "double precision", nullable: false),
                    AnomalyThreshold = table.Column<double>(type: "double precision", nullable: false),
                    AnomalyCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEbmLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEvidentialLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DirichletAlpha0 = table.Column<double>(type: "double precision", nullable: false),
                    DirichletAlpha1 = table.Column<double>(type: "double precision", nullable: false),
                    EpistemicUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    AleatoricUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    TotalUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    BeliefMass = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEvidentialLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEvtGpdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    GpdShape = table.Column<double>(type: "double precision", nullable: false),
                    GpdScale = table.Column<double>(type: "double precision", nullable: false),
                    ExceedanceCount = table.Column<int>(type: "integer", nullable: false),
                    VaR99 = table.Column<double>(type: "double precision", nullable: false),
                    CVaR99 = table.Column<double>(type: "double precision", nullable: false),
                    TailIndex = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEvtGpdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFractionalDiffLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    OptimalD = table.Column<double>(type: "double precision", nullable: false),
                    AdfStatistic = table.Column<double>(type: "double precision", nullable: false),
                    AdfPValue = table.Column<double>(type: "double precision", nullable: false),
                    MemoryRetained = table.Column<double>(type: "double precision", nullable: false),
                    CorrelationWithOriginal = table.Column<double>(type: "double precision", nullable: false),
                    CandleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFractionalDiffLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGjrGarchLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OmegaParam = table.Column<double>(type: "double precision", nullable: false),
                    AlphaParam = table.Column<double>(type: "double precision", nullable: false),
                    GammaParam = table.Column<double>(type: "double precision", nullable: false),
                    BetaParam = table.Column<double>(type: "double precision", nullable: false),
                    ConditionalVolatility = table.Column<double>(type: "double precision", nullable: false),
                    LeverageEffect = table.Column<double>(type: "double precision", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGjrGarchLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGlobalSurrogateLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    SurrogateDepth = table.Column<int>(type: "integer", nullable: false),
                    SurrogateLeaves = table.Column<int>(type: "integer", nullable: false),
                    FidelityAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    SurrogateAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    TopSplitFeature = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AgreementRate = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGlobalSurrogateLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHoeffdingTreeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TreeDepth = table.Column<int>(type: "integer", nullable: false),
                    NodeCount = table.Column<int>(type: "integer", nullable: false),
                    LeafCount = table.Column<int>(type: "integer", nullable: false),
                    SplitCount = table.Column<int>(type: "integer", nullable: false),
                    HoeffdingDelta = table.Column<double>(type: "double precision", nullable: false),
                    TrainAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    TestAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHoeffdingTreeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLInstrumentalVarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    InstrumentName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FirstStageFStat = table.Column<double>(type: "double precision", nullable: false),
                    FirstStageR2 = table.Column<double>(type: "double precision", nullable: false),
                    LateEstimate = table.Column<double>(type: "double precision", nullable: false),
                    LateStdError = table.Column<double>(type: "double precision", nullable: false),
                    SarganPValue = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLInstrumentalVarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLimeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    TopFeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TopFeatureWeight = table.Column<double>(type: "double precision", nullable: false),
                    LocalFidelity = table.Column<double>(type: "double precision", nullable: false),
                    PerturbationCount = table.Column<int>(type: "integer", nullable: false),
                    InterceptValue = table.Column<double>(type: "double precision", nullable: false),
                    KernelWidth = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLimeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMeanCvarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CvarAlpha = table.Column<double>(type: "double precision", nullable: false),
                    OptimalCvar = table.Column<double>(type: "double precision", nullable: false),
                    OptimalReturn = table.Column<double>(type: "double precision", nullable: false),
                    PositionSize = table.Column<double>(type: "double precision", nullable: false),
                    ConstraintBinding = table.Column<bool>(type: "boolean", nullable: false),
                    SolverIterations = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMeanCvarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMfdfaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    HurstMean = table.Column<double>(type: "double precision", nullable: false),
                    HurstMin = table.Column<double>(type: "double precision", nullable: false),
                    HurstMax = table.Column<double>(type: "double precision", nullable: false),
                    MultifractalWidth = table.Column<double>(type: "double precision", nullable: false),
                    ScalingExponentQ2 = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMfdfaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMineLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MutualInformation = table.Column<double>(type: "double precision", nullable: false),
                    MineNetworkLoss = table.Column<double>(type: "double precision", nullable: false),
                    LinearMI = table.Column<double>(type: "double precision", nullable: false),
                    MiGain = table.Column<double>(type: "double precision", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMineLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPassiveAggressiveLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdateCount = table.Column<int>(type: "integer", nullable: false),
                    PassiveCount = table.Column<int>(type: "integer", nullable: false),
                    AggressiveCount = table.Column<int>(type: "integer", nullable: false),
                    MeanHingeLoss = table.Column<double>(type: "double precision", nullable: false),
                    MeanWeightNorm = table.Column<double>(type: "double precision", nullable: false),
                    FinalAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPassiveAggressiveLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRddLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    ThresholdVariable = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CutoffValue = table.Column<double>(type: "double precision", nullable: false),
                    LocalTreatmentEffect = table.Column<double>(type: "double precision", nullable: false),
                    BandwidthUsed = table.Column<double>(type: "double precision", nullable: false),
                    ObservationsLeft = table.Column<int>(type: "integer", nullable: false),
                    ObservationsRight = table.Column<int>(type: "integer", nullable: false),
                    PValue = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRddLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRotationForestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    NumTrees = table.Column<int>(type: "integer", nullable: false),
                    SubsetSize = table.Column<int>(type: "integer", nullable: false),
                    PcaComponents = table.Column<int>(type: "integer", nullable: false),
                    TrainAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    TestAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    DiversityMeasure = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRotationForestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSageLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SageValue = table.Column<double>(type: "double precision", nullable: false),
                    SageRank = table.Column<int>(type: "integer", nullable: false),
                    MarginalContribution = table.Column<double>(type: "double precision", nullable: false),
                    InteractionEffect = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSageLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSamLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    RhoPerturbation = table.Column<double>(type: "double precision", nullable: false),
                    SharpnessValue = table.Column<double>(type: "double precision", nullable: false),
                    FlatnessMeasure = table.Column<double>(type: "double precision", nullable: false),
                    SamLoss = table.Column<double>(type: "double precision", nullable: false),
                    StandardLoss = table.Column<double>(type: "double precision", nullable: false),
                    GeneralizationGap = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSamLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSarimaNeuralLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    SarimaPeriod = table.Column<int>(type: "integer", nullable: false),
                    SarimaAic = table.Column<double>(type: "double precision", nullable: false),
                    ResidualVariance = table.Column<double>(type: "double precision", nullable: false),
                    ResidualMeanAbsError = table.Column<double>(type: "double precision", nullable: false),
                    NeuralAccuracyOnResiduals = table.Column<double>(type: "double precision", nullable: false),
                    HybridAccuracyGain = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSarimaNeuralLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSignatureTransformLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SignatureDepth = table.Column<int>(type: "integer", nullable: false),
                    SignatureLength = table.Column<int>(type: "integer", nullable: false),
                    MeanSignatureNorm = table.Column<double>(type: "double precision", nullable: false),
                    TopFeatureCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    AugmentedDimensions = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSignatureTransformLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTcavLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    ConceptName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TcavScore = table.Column<double>(type: "double precision", nullable: false),
                    LinearAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    StatisticalSignificance = table.Column<double>(type: "double precision", nullable: false),
                    PositiveSensitivity = table.Column<double>(type: "double precision", nullable: false),
                    NegativeSensitivity = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTcavLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTemperatureScalingLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OptimalTemperature = table.Column<double>(type: "double precision", nullable: false),
                    PreCalibrationEce = table.Column<double>(type: "double precision", nullable: false),
                    PostCalibrationEce = table.Column<double>(type: "double precision", nullable: false),
                    PreCalibrationNll = table.Column<double>(type: "double precision", nullable: false),
                    PostCalibrationNll = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTemperatureScalingLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLRollSpreadLogs_Symbol",
                table: "MLRollSpreadLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLAbsorptionRatioLogs_Symbol",
                table: "MLAbsorptionRatioLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLAdwinLogs_MLModelId",
                table: "MLAdwinLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLAmihudLogs_Symbol",
                table: "MLAmihudLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLBetaVaeLogs_MLModelId",
                table: "MLBetaVaeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBlackLittermanLogs_MLModelId",
                table: "MLBlackLittermanLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBocpdLogs_MLModelId",
                table: "MLBocpdLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDesLogs_MLModelId",
                table: "MLDesLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDklLogs_MLModelId",
                table: "MLDklLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEbmLogs_MLModelId",
                table: "MLEbmLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEvidentialLogs_MLModelId",
                table: "MLEvidentialLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEvtGpdLogs_MLModelId",
                table: "MLEvtGpdLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLFractionalDiffLogs_MLModelId",
                table: "MLFractionalDiffLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGjrGarchLogs_MLModelId",
                table: "MLGjrGarchLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGlobalSurrogateLogs_MLModelId",
                table: "MLGlobalSurrogateLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHoeffdingTreeLogs_MLModelId",
                table: "MLHoeffdingTreeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLInstrumentalVarLogs_MLModelId",
                table: "MLInstrumentalVarLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLimeLogs_MLModelId",
                table: "MLLimeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMeanCvarLogs_MLModelId",
                table: "MLMeanCvarLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMfdfaLogs_MLModelId",
                table: "MLMfdfaLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMineLogs_MLModelId",
                table: "MLMineLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPassiveAggressiveLogs_MLModelId",
                table: "MLPassiveAggressiveLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRddLogs_MLModelId",
                table: "MLRddLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRotationForestLogs_MLModelId",
                table: "MLRotationForestLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSageLogs_MLModelId",
                table: "MLSageLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSamLogs_MLModelId",
                table: "MLSamLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSarimaNeuralLogs_MLModelId",
                table: "MLSarimaNeuralLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSignatureTransformLogs_MLModelId",
                table: "MLSignatureTransformLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTcavLogs_MLModelId",
                table: "MLTcavLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTemperatureScalingLogs_MLModelId",
                table: "MLTemperatureScalingLogs",
                column: "MLModelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLAbsorptionRatioLogs");

            migrationBuilder.DropTable(
                name: "MLAdwinLogs");

            migrationBuilder.DropTable(
                name: "MLAmihudLogs");

            migrationBuilder.DropTable(
                name: "MLBetaVaeLogs");

            migrationBuilder.DropTable(
                name: "MLBlackLittermanLogs");

            migrationBuilder.DropTable(
                name: "MLBocpdLogs");

            migrationBuilder.DropTable(
                name: "MLDesLogs");

            migrationBuilder.DropTable(
                name: "MLDklLogs");

            migrationBuilder.DropTable(
                name: "MLEbmLogs");

            migrationBuilder.DropTable(
                name: "MLEvidentialLogs");

            migrationBuilder.DropTable(
                name: "MLEvtGpdLogs");

            migrationBuilder.DropTable(
                name: "MLFractionalDiffLogs");

            migrationBuilder.DropTable(
                name: "MLGjrGarchLogs");

            migrationBuilder.DropTable(
                name: "MLGlobalSurrogateLogs");

            migrationBuilder.DropTable(
                name: "MLHoeffdingTreeLogs");

            migrationBuilder.DropTable(
                name: "MLInstrumentalVarLogs");

            migrationBuilder.DropTable(
                name: "MLLimeLogs");

            migrationBuilder.DropTable(
                name: "MLMeanCvarLogs");

            migrationBuilder.DropTable(
                name: "MLMfdfaLogs");

            migrationBuilder.DropTable(
                name: "MLMineLogs");

            migrationBuilder.DropTable(
                name: "MLPassiveAggressiveLogs");

            migrationBuilder.DropTable(
                name: "MLRddLogs");

            migrationBuilder.DropTable(
                name: "MLRotationForestLogs");

            migrationBuilder.DropTable(
                name: "MLSageLogs");

            migrationBuilder.DropTable(
                name: "MLSamLogs");

            migrationBuilder.DropTable(
                name: "MLSarimaNeuralLogs");

            migrationBuilder.DropTable(
                name: "MLSignatureTransformLogs");

            migrationBuilder.DropTable(
                name: "MLTcavLogs");

            migrationBuilder.DropTable(
                name: "MLTemperatureScalingLogs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MLRollSpreadLogs",
                table: "MLRollSpreadLogs");

            migrationBuilder.DropIndex(
                name: "IX_MLRollSpreadLogs_Symbol",
                table: "MLRollSpreadLogs");

            migrationBuilder.DropColumn(
                name: "EffectiveBidAskBps",
                table: "MLRollSpreadLogs");

            migrationBuilder.DropColumn(
                name: "ReturnAutocovariance",
                table: "MLRollSpreadLogs");

            migrationBuilder.DropColumn(
                name: "RollSpreadEstimate",
                table: "MLRollSpreadLogs");

            migrationBuilder.DropColumn(
                name: "SampleSize",
                table: "MLRollSpreadLogs");

            migrationBuilder.DropColumn(
                name: "SpreadAlert",
                table: "MLRollSpreadLogs");

            migrationBuilder.RenameTable(
                name: "MLRollSpreadLogs",
                newName: "MLRollSpreadLog");

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "MLRollSpreadLog",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddPrimaryKey(
                name: "PK_MLRollSpreadLog",
                table: "MLRollSpreadLog",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_MLRollSpreadLog_Symbol_Timeframe",
                table: "MLRollSpreadLog",
                columns: new[] { "Symbol", "Timeframe" });
        }
    }
}
