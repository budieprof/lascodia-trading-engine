using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations19 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLAlmgrenChrissLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TemporaryImpactParam = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    PermanentImpactParam = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OptimalExecutionIntervals = table.Column<int>(type: "integer", nullable: false),
                    ExpectedExecutionCost = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ExecutionRisk = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OptimalUrgency = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RiskAversion = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAlmgrenChrissLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBipowerVariationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RealizedVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    BipowerVariation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    JumpComponent = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    JumpRatio = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    TriPowerQuarticity = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ContinuousVariation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsJumpDay = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBipowerVariationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBivariateEvtLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TailDependenceLower = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    TailDependenceUpper = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ChiStatistic = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ExtremeCorrelation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ThresholdUsed = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBivariateEvtLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBvarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    VarOrder = table.Column<int>(type: "integer", nullable: false),
                    PairCount = table.Column<int>(type: "integer", nullable: false),
                    MnPriorLambda = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MnPriorTightness = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    InsampleMse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OosDirectionAccuracy = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ConditionNumber = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBvarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLClarkWestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId1 = table.Column<long>(type: "bigint", nullable: false),
                    MLModelId2 = table.Column<long>(type: "bigint", nullable: false),
                    CwStatistic = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    PValue = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    BenchmarkMse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ChallengerMse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false),
                    IsChallengerBetter = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLClarkWestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCojumpLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CojumpCount = table.Column<int>(type: "integer", nullable: false),
                    IndividualJumpRate1 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IndividualJumpRate2 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ExpectedCojumpRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ObservedCojumpRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    CojumpZScore = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsSignificant = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCojumpLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDccaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DccaScale4 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DccaScale16 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DccaScale64 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    CrossoverScale = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MeanDccaCoefficient = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    LongRangeCorrelation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDccaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDccGarchLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DynamicCorrelation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    AlphaParam = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    BetaParam = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    UnconditionalCorrelation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    CorrelationChange = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDccGarchLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLErgodicityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    EnsembleGrowthRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    TimeAverageGrowthRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ErgodicityGap = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    NaiveKellyFraction = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ErgodicityAdjustedKelly = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GrowthRateVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLErgodicityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGarchMLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GarchMAlpha = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GarchMBeta = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GarchMOmega = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RiskPremiumLambda = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    LambdaTStatistic = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsPositiveRiskPremium = table.Column<bool>(type: "boolean", nullable: false),
                    PredictedRiskPremium = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ConditionalVarianceForecast = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGarchMLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHarRvLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HarBetaDaily = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    HarBetaWeekly = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    HarBetaMonthly = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    HarBeta0 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ForecastedRv = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RealizedRvDaily = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    InsampleRmse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHarRvLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLIntradaySeasonalityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FourierHarmonics = table.Column<int>(type: "integer", nullable: false),
                    SeasonalityStrength = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    PeakSeasonalHour = table.Column<int>(type: "integer", nullable: false),
                    TroughSeasonalHour = table.Column<int>(type: "integer", nullable: false),
                    SeasonalAmplitude = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    FourierCoefficientsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLIntradaySeasonalityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLJumpTestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    JumpCount = table.Column<int>(type: "integer", nullable: false),
                    JumpContributionToVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MeanJumpSize = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MaxJumpSize = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    LocalBipowerVariation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    TotalVariation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    AlphaLevel = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLJumpTestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMidasLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    MidasBeta0 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MidasBeta1 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    AlmonTheta1 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    AlmonTheta2 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MeanWeightDecayHalfLife = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    InsampleR2 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OptimalLagCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMidasLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNeuralGrangerLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CausingSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GrangerCausalEffect = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    FullModelMse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ReducedModelMse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    PValue = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsSignificant = table.Column<bool>(type: "boolean", nullable: false),
                    MaxLagTested = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNeuralGrangerLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRealizedQuarticityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RealizedVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RealizedQuarticity = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    QuarticityRatio = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    VolOfVol = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RvConfidenceIntervalLow = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RvConfidenceIntervalHigh = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRealizedQuarticityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSemivarianceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RealizedVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    UpsideSemivariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DownsideSemivariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SignedVariation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DownsideConcentration = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SemivarianceRatio = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSemivarianceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSuperstatisticsLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GammaShapeK = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GammaThetaScale = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    EffectiveDegreesOfFreedom = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    StudentTFitKl = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsHeavyTailed = table.Column<bool>(type: "boolean", nullable: false),
                    LocalVarianceCoeffVar = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SuperstatsFitScore = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSuperstatisticsLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLVrpLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpectedVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RealizedVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    VarianceRiskPremium = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    VrpZScore = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Horizon1ForecastR2 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Horizon5ForecastR2 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ForecastBeta = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLVrpLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLWaveletCoherenceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CoherenceScale5 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    CoherenceScale60 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    CoherenceScale240 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MeanPhaseAngle = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    PhaseLockingRatio = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DominantCoherenceScale = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLWaveletCoherenceLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLAlmgrenChrissLogs_Symbol_ComputedAt",
                table: "MLAlmgrenChrissLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLBipowerVariationLogs_Symbol_ComputedAt",
                table: "MLBipowerVariationLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLBivariateEvtLogs_Symbol_Symbol2_ComputedAt",
                table: "MLBivariateEvtLogs",
                columns: new[] { "Symbol", "Symbol2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLBvarLogs_Symbol_ComputedAt",
                table: "MLBvarLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLClarkWestLogs_MLModelId1_MLModelId2_ComputedAt",
                table: "MLClarkWestLogs",
                columns: new[] { "MLModelId1", "MLModelId2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCojumpLogs_Symbol_Symbol2_ComputedAt",
                table: "MLCojumpLogs",
                columns: new[] { "Symbol", "Symbol2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLDccaLogs_Symbol_Symbol2_ComputedAt",
                table: "MLDccaLogs",
                columns: new[] { "Symbol", "Symbol2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLDccGarchLogs_Symbol_Symbol2_ComputedAt",
                table: "MLDccGarchLogs",
                columns: new[] { "Symbol", "Symbol2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLErgodicityLogs_MLModelId_ComputedAt",
                table: "MLErgodicityLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLGarchMLogs_Symbol_ComputedAt",
                table: "MLGarchMLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLHarRvLogs_Symbol_ComputedAt",
                table: "MLHarRvLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLIntradaySeasonalityLogs_Symbol_ComputedAt",
                table: "MLIntradaySeasonalityLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLJumpTestLogs_Symbol_ComputedAt",
                table: "MLJumpTestLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMidasLogs_Symbol_ComputedAt",
                table: "MLMidasLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLNeuralGrangerLogs_Symbol_CausingSymbol_ComputedAt",
                table: "MLNeuralGrangerLogs",
                columns: new[] { "Symbol", "CausingSymbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRealizedQuarticityLogs_Symbol_ComputedAt",
                table: "MLRealizedQuarticityLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSemivarianceLogs_Symbol_ComputedAt",
                table: "MLSemivarianceLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSuperstatisticsLogs_Symbol_ComputedAt",
                table: "MLSuperstatisticsLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLVrpLogs_Symbol_ComputedAt",
                table: "MLVrpLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLWaveletCoherenceLogs_Symbol_Symbol2_ComputedAt",
                table: "MLWaveletCoherenceLogs",
                columns: new[] { "Symbol", "Symbol2", "ComputedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLAlmgrenChrissLogs");

            migrationBuilder.DropTable(
                name: "MLBipowerVariationLogs");

            migrationBuilder.DropTable(
                name: "MLBivariateEvtLogs");

            migrationBuilder.DropTable(
                name: "MLBvarLogs");

            migrationBuilder.DropTable(
                name: "MLClarkWestLogs");

            migrationBuilder.DropTable(
                name: "MLCojumpLogs");

            migrationBuilder.DropTable(
                name: "MLDccaLogs");

            migrationBuilder.DropTable(
                name: "MLDccGarchLogs");

            migrationBuilder.DropTable(
                name: "MLErgodicityLogs");

            migrationBuilder.DropTable(
                name: "MLGarchMLogs");

            migrationBuilder.DropTable(
                name: "MLHarRvLogs");

            migrationBuilder.DropTable(
                name: "MLIntradaySeasonalityLogs");

            migrationBuilder.DropTable(
                name: "MLJumpTestLogs");

            migrationBuilder.DropTable(
                name: "MLMidasLogs");

            migrationBuilder.DropTable(
                name: "MLNeuralGrangerLogs");

            migrationBuilder.DropTable(
                name: "MLRealizedQuarticityLogs");

            migrationBuilder.DropTable(
                name: "MLSemivarianceLogs");

            migrationBuilder.DropTable(
                name: "MLSuperstatisticsLogs");

            migrationBuilder.DropTable(
                name: "MLVrpLogs");

            migrationBuilder.DropTable(
                name: "MLWaveletCoherenceLogs");
        }
    }
}
