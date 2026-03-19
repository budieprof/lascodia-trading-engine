using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations22 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MLTdaLogs_MLModel_MLModelId",
                table: "MLTdaLogs");

            migrationBuilder.DropIndex(
                name: "IX_MLTdaLogs_MLModelId",
                table: "MLTdaLogs");

            migrationBuilder.DropIndex(
                name: "IX_MLRoughVolLogs_MLModelId_ComputedAt",
                table: "MLRoughVolLogs");

            migrationBuilder.DropIndex(
                name: "IX_MLRealizedKernelLogs_MLModelId",
                table: "MLRealizedKernelLogs");

            migrationBuilder.DropIndex(
                name: "IX_MLPcmciLogs_Symbol_ComputedAt",
                table: "MLPcmciLogs");

            migrationBuilder.DropIndex(
                name: "IX_MLMidasLogs_Symbol_ComputedAt",
                table: "MLMidasLogs");

            migrationBuilder.DropIndex(
                name: "IX_MLIntradaySeasonalityLogs_Symbol_ComputedAt",
                table: "MLIntradaySeasonalityLogs");

            migrationBuilder.DropColumn(
                name: "BettiOne",
                table: "MLTdaLogs");

            migrationBuilder.DropColumn(
                name: "BettiZero",
                table: "MLTdaLogs");

            migrationBuilder.DropColumn(
                name: "FilteredRadius",
                table: "MLTdaLogs");

            migrationBuilder.DropColumn(
                name: "MLModelId",
                table: "MLTdaLogs");

            migrationBuilder.DropColumn(
                name: "MaxPersistence",
                table: "MLTdaLogs");

            migrationBuilder.DropColumn(
                name: "Timeframe",
                table: "MLTdaLogs");

            migrationBuilder.DropColumn(
                name: "BergomiXi",
                table: "MLRoughVolLogs");

            migrationBuilder.DropColumn(
                name: "ClassicalVol",
                table: "MLRoughVolLogs");

            migrationBuilder.DropColumn(
                name: "MLModelId",
                table: "MLRoughVolLogs");

            migrationBuilder.DropColumn(
                name: "RoughVol",
                table: "MLRoughVolLogs");

            migrationBuilder.DropColumn(
                name: "RoughnessDifference",
                table: "MLRoughVolLogs");

            migrationBuilder.DropColumn(
                name: "SampleCount",
                table: "MLRoughVolLogs");

            migrationBuilder.DropColumn(
                name: "BandwidthH",
                table: "MLRealizedKernelLogs");

            migrationBuilder.DropColumn(
                name: "CandleCount",
                table: "MLRealizedKernelLogs");

            migrationBuilder.DropColumn(
                name: "KernelWeight",
                table: "MLRealizedKernelLogs");

            migrationBuilder.DropColumn(
                name: "MLModelId",
                table: "MLRealizedKernelLogs");

            migrationBuilder.DropColumn(
                name: "MicrostructureNoise",
                table: "MLRealizedKernelLogs");

            migrationBuilder.DropColumn(
                name: "MicrostructureNoiseEstimate",
                table: "MLRealizedKernelLogs");

            migrationBuilder.DropColumn(
                name: "RealizedVariance",
                table: "MLRealizedKernelLogs");

            migrationBuilder.DropColumn(
                name: "Timeframe",
                table: "MLRealizedKernelLogs");

            migrationBuilder.DropColumn(
                name: "CausalGraphJson",
                table: "MLPcmciLogs");

            migrationBuilder.DropColumn(
                name: "FalsePositiveRate",
                table: "MLPcmciLogs");

            migrationBuilder.DropColumn(
                name: "MaxLagTested",
                table: "MLPcmciLogs");

            migrationBuilder.DropColumn(
                name: "MciTestCount",
                table: "MLPcmciLogs");

            migrationBuilder.DropColumn(
                name: "AlmonTheta1",
                table: "MLMidasLogs");

            migrationBuilder.DropColumn(
                name: "AlmonTheta2",
                table: "MLMidasLogs");

            migrationBuilder.DropColumn(
                name: "InsampleR2",
                table: "MLMidasLogs");

            migrationBuilder.DropColumn(
                name: "MeanWeightDecayHalfLife",
                table: "MLMidasLogs");

            migrationBuilder.DropColumn(
                name: "MidasBeta0",
                table: "MLMidasLogs");

            migrationBuilder.DropColumn(
                name: "FourierCoefficientsJson",
                table: "MLIntradaySeasonalityLogs");

            migrationBuilder.DropColumn(
                name: "FourierHarmonics",
                table: "MLIntradaySeasonalityLogs");

            migrationBuilder.DropColumn(
                name: "PeakSeasonalHour",
                table: "MLIntradaySeasonalityLogs");

            migrationBuilder.DropColumn(
                name: "SeasonalAmplitude",
                table: "MLIntradaySeasonalityLogs");

            migrationBuilder.DropColumn(
                name: "SeasonalityStrength",
                table: "MLIntradaySeasonalityLogs");

            migrationBuilder.RenameColumn(
                name: "PointCloudSize",
                table: "MLTdaLogs",
                newName: "Betti1");

            migrationBuilder.RenameColumn(
                name: "MeanPersistence",
                table: "MLTdaLogs",
                newName: "LongestBarcode");

            migrationBuilder.RenameColumn(
                name: "RealizedVol",
                table: "MLRealizedKernelLogs",
                newName: "AsymptoticVariance");

            migrationBuilder.RenameColumn(
                name: "KernelBandwidth",
                table: "MLRealizedKernelLogs",
                newName: "OptimalBandwidth");

            migrationBuilder.RenameColumn(
                name: "SignificantEdgeCount",
                table: "MLPcmciLogs",
                newName: "CausalLag");

            migrationBuilder.RenameColumn(
                name: "OptimalLagCount",
                table: "MLMidasLogs",
                newName: "LfTargetHorizon");

            migrationBuilder.RenameColumn(
                name: "TroughSeasonalHour",
                table: "MLIntradaySeasonalityLogs",
                newName: "HourOfDay");

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "MLTdaLogs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<int>(
                name: "Betti0",
                table: "MLTdaLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "MLRoughVolLogs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<double>(
                name: "HurstExponent",
                table: "MLRoughVolLogs",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,8)");

            migrationBuilder.AddColumn<double>(
                name: "LogLogSlope",
                table: "MLRoughVolLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "RoughnessIndex",
                table: "MLRoughVolLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "VolForecast",
                table: "MLRoughVolLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "KernelType",
                table: "MLRealizedKernelLogs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "MLPcmciLogs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<string>(
                name: "CauseSymbol",
                table: "MLPcmciLogs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EffectSymbol",
                table: "MLPcmciLogs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsSignificant",
                table: "MLPcmciLogs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "MciPValue",
                table: "MLPcmciLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "PartialCorr",
                table: "MLPcmciLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "MLMidasLogs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<double>(
                name: "MidasBeta1",
                table: "MLMidasLogs",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,8)");

            migrationBuilder.AddColumn<double>(
                name: "FitRSquared",
                table: "MLMidasLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "HfPredictorType",
                table: "MLMidasLogs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MidasBeta2",
                table: "MLMidasLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "FffCoeff1",
                table: "MLIntradaySeasonalityLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "FffCoeff2",
                table: "MLIntradaySeasonalityLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "SeasonalVolFactor",
                table: "MLIntradaySeasonalityLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "VolumeClockBar",
                table: "MLIntradaySeasonalityLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "MLAdaptiveConformalLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AciAlpha = table.Column<double>(type: "double precision", nullable: false),
                    PredictionIntervalLow = table.Column<double>(type: "double precision", nullable: false),
                    PredictionIntervalHigh = table.Column<double>(type: "double precision", nullable: false),
                    RecentCoverage = table.Column<double>(type: "double precision", nullable: false),
                    AdaptationRate = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdaptiveConformalLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBatesCalibLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JumpIntensity = table.Column<double>(type: "double precision", nullable: false),
                    JumpMeanSize = table.Column<double>(type: "double precision", nullable: false),
                    JumpVolatility = table.Column<double>(type: "double precision", nullable: false),
                    HestonVol = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationError = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBatesCalibLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEmdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Imf1Energy = table.Column<double>(type: "double precision", nullable: false),
                    Imf2Energy = table.Column<double>(type: "double precision", nullable: false),
                    Imf3Energy = table.Column<double>(type: "double precision", nullable: false),
                    TrendEnergy = table.Column<double>(type: "double precision", nullable: false),
                    InstantaneousFreq = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEmdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGramCharlierLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GcSkewness = table.Column<double>(type: "double precision", nullable: false),
                    GcKurtosis = table.Column<double>(type: "double precision", nullable: false),
                    GcVar95 = table.Column<double>(type: "double precision", nullable: false),
                    GcCvar95 = table.Column<double>(type: "double precision", nullable: false),
                    GcLogLik = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGramCharlierLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHayashiYoshidaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LeadSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LagSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LeadLagSeconds = table.Column<double>(type: "double precision", nullable: false),
                    HayashiYoshidaCorr = table.Column<double>(type: "double precision", nullable: false),
                    EppsRatio = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHayashiYoshidaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHrpConvexLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConvexCluster = table.Column<int>(type: "integer", nullable: false),
                    HrpWeight = table.Column<double>(type: "double precision", nullable: false),
                    ClusteringLambda = table.Column<double>(type: "double precision", nullable: false),
                    DendrogramLevel = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHrpConvexLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLassoGrangerLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CauseSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EffectSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GrangerPValue = table.Column<double>(type: "double precision", nullable: false),
                    LassoCoefficient = table.Column<double>(type: "double precision", nullable: false),
                    LagOrder = table.Column<int>(type: "integer", nullable: false),
                    IsSignificant = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLassoGrangerLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLevyProcessLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VgSigma = table.Column<double>(type: "double precision", nullable: false),
                    VgNu = table.Column<double>(type: "double precision", nullable: false),
                    VgTheta = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationLogLik = table.Column<double>(type: "double precision", nullable: false),
                    ExcessKurtosis = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLevyProcessLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPathSignatureLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Sig11 = table.Column<double>(type: "double precision", nullable: false),
                    Sig12 = table.Column<double>(type: "double precision", nullable: false),
                    Sig21 = table.Column<double>(type: "double precision", nullable: false),
                    Sig22 = table.Column<double>(type: "double precision", nullable: false),
                    SignatureNorm = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPathSignatureLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLScorecardLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    StrategyHealthScore = table.Column<double>(type: "double precision", nullable: false),
                    MlAccuracyScore = table.Column<double>(type: "double precision", nullable: false),
                    SharpeScore = table.Column<double>(type: "double precision", nullable: false),
                    ExecutionScore = table.Column<double>(type: "double precision", nullable: false),
                    RegimeAlignmentScore = table.Column<double>(type: "double precision", nullable: false),
                    DrawdownScore = table.Column<double>(type: "double precision", nullable: false),
                    CompositeGrade = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLScorecardLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLStatArbPcaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OuMeanReversionSpeed = table.Column<double>(type: "double precision", nullable: false),
                    OuLongRunMean = table.Column<double>(type: "double precision", nullable: false),
                    SpreadZScore = table.Column<double>(type: "double precision", nullable: false),
                    PcaResidual = table.Column<double>(type: "double precision", nullable: false),
                    SignalStrength = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStatArbPcaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTsrvLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TsrvEstimate = table.Column<double>(type: "double precision", nullable: false),
                    RvFast = table.Column<double>(type: "double precision", nullable: false),
                    RvSlow = table.Column<double>(type: "double precision", nullable: false),
                    NoiseVariance = table.Column<double>(type: "double precision", nullable: false),
                    OptimalScale = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTsrvLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLUniversalPortfolioLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UniversalWeight = table.Column<double>(type: "double precision", nullable: false),
                    CumulativeReturn = table.Column<double>(type: "double precision", nullable: false),
                    BestCrpBenchmark = table.Column<double>(type: "double precision", nullable: false),
                    RegretBound = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLUniversalPortfolioLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLWassersteinLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WassersteinDist = table.Column<double>(type: "double precision", nullable: false),
                    TrainWindowDays = table.Column<int>(type: "integer", nullable: false),
                    TestWindowDays = table.Column<int>(type: "integer", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    ThresholdUsed = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLWassersteinLogs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLAdaptiveConformalLogs");

            migrationBuilder.DropTable(
                name: "MLBatesCalibLogs");

            migrationBuilder.DropTable(
                name: "MLEmdLogs");

            migrationBuilder.DropTable(
                name: "MLGramCharlierLogs");

            migrationBuilder.DropTable(
                name: "MLHayashiYoshidaLogs");

            migrationBuilder.DropTable(
                name: "MLHrpConvexLogs");

            migrationBuilder.DropTable(
                name: "MLLassoGrangerLogs");

            migrationBuilder.DropTable(
                name: "MLLevyProcessLogs");

            migrationBuilder.DropTable(
                name: "MLPathSignatureLogs");

            migrationBuilder.DropTable(
                name: "MLScorecardLogs");

            migrationBuilder.DropTable(
                name: "MLStatArbPcaLogs");

            migrationBuilder.DropTable(
                name: "MLTsrvLogs");

            migrationBuilder.DropTable(
                name: "MLUniversalPortfolioLogs");

            migrationBuilder.DropTable(
                name: "MLWassersteinLogs");

            migrationBuilder.DropColumn(
                name: "Betti0",
                table: "MLTdaLogs");

            migrationBuilder.DropColumn(
                name: "LogLogSlope",
                table: "MLRoughVolLogs");

            migrationBuilder.DropColumn(
                name: "RoughnessIndex",
                table: "MLRoughVolLogs");

            migrationBuilder.DropColumn(
                name: "VolForecast",
                table: "MLRoughVolLogs");

            migrationBuilder.DropColumn(
                name: "KernelType",
                table: "MLRealizedKernelLogs");

            migrationBuilder.DropColumn(
                name: "CauseSymbol",
                table: "MLPcmciLogs");

            migrationBuilder.DropColumn(
                name: "EffectSymbol",
                table: "MLPcmciLogs");

            migrationBuilder.DropColumn(
                name: "IsSignificant",
                table: "MLPcmciLogs");

            migrationBuilder.DropColumn(
                name: "MciPValue",
                table: "MLPcmciLogs");

            migrationBuilder.DropColumn(
                name: "PartialCorr",
                table: "MLPcmciLogs");

            migrationBuilder.DropColumn(
                name: "FitRSquared",
                table: "MLMidasLogs");

            migrationBuilder.DropColumn(
                name: "HfPredictorType",
                table: "MLMidasLogs");

            migrationBuilder.DropColumn(
                name: "MidasBeta2",
                table: "MLMidasLogs");

            migrationBuilder.DropColumn(
                name: "FffCoeff1",
                table: "MLIntradaySeasonalityLogs");

            migrationBuilder.DropColumn(
                name: "FffCoeff2",
                table: "MLIntradaySeasonalityLogs");

            migrationBuilder.DropColumn(
                name: "SeasonalVolFactor",
                table: "MLIntradaySeasonalityLogs");

            migrationBuilder.DropColumn(
                name: "VolumeClockBar",
                table: "MLIntradaySeasonalityLogs");

            migrationBuilder.RenameColumn(
                name: "LongestBarcode",
                table: "MLTdaLogs",
                newName: "MeanPersistence");

            migrationBuilder.RenameColumn(
                name: "Betti1",
                table: "MLTdaLogs",
                newName: "PointCloudSize");

            migrationBuilder.RenameColumn(
                name: "OptimalBandwidth",
                table: "MLRealizedKernelLogs",
                newName: "KernelBandwidth");

            migrationBuilder.RenameColumn(
                name: "AsymptoticVariance",
                table: "MLRealizedKernelLogs",
                newName: "RealizedVol");

            migrationBuilder.RenameColumn(
                name: "CausalLag",
                table: "MLPcmciLogs",
                newName: "SignificantEdgeCount");

            migrationBuilder.RenameColumn(
                name: "LfTargetHorizon",
                table: "MLMidasLogs",
                newName: "OptimalLagCount");

            migrationBuilder.RenameColumn(
                name: "HourOfDay",
                table: "MLIntradaySeasonalityLogs",
                newName: "TroughSeasonalHour");

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "MLTdaLogs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<double>(
                name: "BettiOne",
                table: "MLTdaLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "BettiZero",
                table: "MLTdaLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "FilteredRadius",
                table: "MLTdaLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<long>(
                name: "MLModelId",
                table: "MLTdaLogs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<double>(
                name: "MaxPersistence",
                table: "MLTdaLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "Timeframe",
                table: "MLTdaLogs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "MLRoughVolLogs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<decimal>(
                name: "HurstExponent",
                table: "MLRoughVolLogs",
                type: "numeric(18,8)",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddColumn<decimal>(
                name: "BergomiXi",
                table: "MLRoughVolLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ClassicalVol",
                table: "MLRoughVolLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "MLModelId",
                table: "MLRoughVolLogs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "RoughVol",
                table: "MLRoughVolLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RoughnessDifference",
                table: "MLRoughVolLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "SampleCount",
                table: "MLRoughVolLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BandwidthH",
                table: "MLRealizedKernelLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CandleCount",
                table: "MLRealizedKernelLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "KernelWeight",
                table: "MLRealizedKernelLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<long>(
                name: "MLModelId",
                table: "MLRealizedKernelLogs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<double>(
                name: "MicrostructureNoise",
                table: "MLRealizedKernelLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "MicrostructureNoiseEstimate",
                table: "MLRealizedKernelLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "RealizedVariance",
                table: "MLRealizedKernelLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "Timeframe",
                table: "MLRealizedKernelLogs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "MLPcmciLogs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<string>(
                name: "CausalGraphJson",
                table: "MLPcmciLogs",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "FalsePositiveRate",
                table: "MLPcmciLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "MaxLagTested",
                table: "MLPcmciLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MciTestCount",
                table: "MLPcmciLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "MLMidasLogs",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<decimal>(
                name: "MidasBeta1",
                table: "MLMidasLogs",
                type: "numeric(18,8)",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddColumn<decimal>(
                name: "AlmonTheta1",
                table: "MLMidasLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AlmonTheta2",
                table: "MLMidasLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "InsampleR2",
                table: "MLMidasLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MeanWeightDecayHalfLife",
                table: "MLMidasLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MidasBeta0",
                table: "MLMidasLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "FourierCoefficientsJson",
                table: "MLIntradaySeasonalityLogs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FourierHarmonics",
                table: "MLIntradaySeasonalityLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PeakSeasonalHour",
                table: "MLIntradaySeasonalityLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "SeasonalAmplitude",
                table: "MLIntradaySeasonalityLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SeasonalityStrength",
                table: "MLIntradaySeasonalityLogs",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_MLTdaLogs_MLModelId",
                table: "MLTdaLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRoughVolLogs_MLModelId_ComputedAt",
                table: "MLRoughVolLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRealizedKernelLogs_MLModelId",
                table: "MLRealizedKernelLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPcmciLogs_Symbol_ComputedAt",
                table: "MLPcmciLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMidasLogs_Symbol_ComputedAt",
                table: "MLMidasLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLIntradaySeasonalityLogs_Symbol_ComputedAt",
                table: "MLIntradaySeasonalityLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_MLTdaLogs_MLModel_MLModelId",
                table: "MLTdaLogs",
                column: "MLModelId",
                principalTable: "MLModel",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
