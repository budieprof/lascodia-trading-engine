using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations20 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLCarrLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConditionalRange = table.Column<double>(type: "double precision", nullable: false),
                    RangeForecast = table.Column<double>(type: "double precision", nullable: false),
                    AlphaParam = table.Column<double>(type: "double precision", nullable: false),
                    BetaParam = table.Column<double>(type: "double precision", nullable: false),
                    OmegaParam = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCarrLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCarryDecompLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CarryComponent = table.Column<double>(type: "double precision", nullable: false),
                    ExchangeRateComponent = table.Column<double>(type: "double precision", nullable: false),
                    CrashRiskPremium = table.Column<double>(type: "double precision", nullable: false),
                    InterestDifferential = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCarryDecompLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCipLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CipBasis = table.Column<double>(type: "double precision", nullable: false),
                    ForwardPremium = table.Column<double>(type: "double precision", nullable: false),
                    IsArbitrageable = table.Column<bool>(type: "boolean", nullable: false),
                    InterestDifferential = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCipLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCornishFisherLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CfVaR95 = table.Column<double>(type: "double precision", nullable: false),
                    CfES99 = table.Column<double>(type: "double precision", nullable: false),
                    GaussianVaR95 = table.Column<double>(type: "double precision", nullable: false),
                    Skewness = table.Column<double>(type: "double precision", nullable: false),
                    ExcessKurtosis = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCornishFisherLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCorwinSchultzLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SpreadEstimate = table.Column<double>(type: "double precision", nullable: false),
                    SpreadPct = table.Column<double>(type: "double precision", nullable: false),
                    IsLiquid = table.Column<bool>(type: "boolean", nullable: false),
                    WindowBars = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCorwinSchultzLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEntropyPoolingLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PosteriorWeight = table.Column<double>(type: "double precision", nullable: false),
                    KlDivergence = table.Column<double>(type: "double precision", nullable: false),
                    ViewStrength = table.Column<double>(type: "double precision", nullable: false),
                    PriorWeight = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEntropyPoolingLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEstarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EstarGamma = table.Column<double>(type: "double precision", nullable: false),
                    Equilibrium = table.Column<double>(type: "double precision", nullable: false),
                    MeanReversionSpeed = table.Column<double>(type: "double precision", nullable: false),
                    IsCointegrated = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEstarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEventStudyLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Car = table.Column<double>(type: "double precision", nullable: false),
                    AbnormalReturn = table.Column<double>(type: "double precision", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEventStudyLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFactorCopulaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FactorLoading = table.Column<double>(type: "double precision", nullable: false),
                    JointTailProb = table.Column<double>(type: "double precision", nullable: false),
                    SystemicCrashProb = table.Column<double>(type: "double precision", nullable: false),
                    FactorCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFactorCopulaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGarchEvtCopulaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JointVaR95 = table.Column<double>(type: "double precision", nullable: false),
                    JointES99 = table.Column<double>(type: "double precision", nullable: false),
                    CopulaCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    PairCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGarchEvtCopulaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGonzaloGrangerLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GgContribution = table.Column<double>(type: "double precision", nullable: false),
                    IsLeader = table.Column<bool>(type: "boolean", nullable: false),
                    CointegrationPair = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    VecmAlpha = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGonzaloGrangerLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHansenSkewedTLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SkewnessParam = table.Column<double>(type: "double precision", nullable: false),
                    TailParam = table.Column<double>(type: "double precision", nullable: false),
                    VolatilityForecast = table.Column<double>(type: "double precision", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHansenSkewedTLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLVaRLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LiquidityAdjVaR95 = table.Column<double>(type: "double precision", nullable: false),
                    MarketVaR95 = table.Column<double>(type: "double precision", nullable: false),
                    SpreadCost = table.Column<double>(type: "double precision", nullable: false),
                    MarketImpactCost = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLVaRLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNowcastLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NowcastValue = table.Column<double>(type: "double precision", nullable: false),
                    NowcastSurprise = table.Column<double>(type: "double precision", nullable: false),
                    MacroVariable = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PredictionError = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNowcastLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRegimeCAPMLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RegimeBeta = table.Column<double>(type: "double precision", nullable: false),
                    Regime = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AlphaEstimate = table.Column<double>(type: "double precision", nullable: false),
                    RSquared = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRegimeCAPMLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRobustCovLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RobustVariance = table.Column<double>(type: "double precision", nullable: false),
                    SampleVariance = table.Column<double>(type: "double precision", nullable: false),
                    OutlierFraction = table.Column<double>(type: "double precision", nullable: false),
                    VarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRobustCovLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSkewRpLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SkewnessRp = table.Column<double>(type: "double precision", nullable: false),
                    RealizedSkewness = table.Column<double>(type: "double precision", nullable: false),
                    ImpliedSkewness = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSkewRpLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSpilloverLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NetSpillover = table.Column<double>(type: "double precision", nullable: false),
                    TotalSpillover = table.Column<double>(type: "double precision", nullable: false),
                    SymbolCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSpilloverLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLStochVolLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LogVolMean = table.Column<double>(type: "double precision", nullable: false),
                    LogVolVariance = table.Column<double>(type: "double precision", nullable: false),
                    VolatilityForecast = table.Column<double>(type: "double precision", nullable: false),
                    PersistenceParam = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStochVolLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTarchLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AsymmetryGamma = table.Column<double>(type: "double precision", nullable: false),
                    VolatilityForecast = table.Column<double>(type: "double precision", nullable: false),
                    LeverageEffect = table.Column<double>(type: "double precision", nullable: false),
                    GarchAlpha = table.Column<double>(type: "double precision", nullable: false),
                    GarchBeta = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTarchLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLCarrLogs_MLModelId_ComputedAt",
                table: "MLCarrLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCarryDecompLogs_MLModelId_ComputedAt",
                table: "MLCarryDecompLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCipLogs_MLModelId_ComputedAt",
                table: "MLCipLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCornishFisherLogs_MLModelId_ComputedAt",
                table: "MLCornishFisherLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCorwinSchultzLogs_MLModelId_ComputedAt",
                table: "MLCorwinSchultzLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEntropyPoolingLogs_MLModelId_ComputedAt",
                table: "MLEntropyPoolingLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEstarLogs_MLModelId_ComputedAt",
                table: "MLEstarLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEventStudyLogs_MLModelId_ComputedAt",
                table: "MLEventStudyLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFactorCopulaLogs_MLModelId_ComputedAt",
                table: "MLFactorCopulaLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLGarchEvtCopulaLogs_MLModelId_ComputedAt",
                table: "MLGarchEvtCopulaLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLGonzaloGrangerLogs_MLModelId_ComputedAt",
                table: "MLGonzaloGrangerLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLHansenSkewedTLogs_MLModelId_ComputedAt",
                table: "MLHansenSkewedTLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLVaRLogs_MLModelId_ComputedAt",
                table: "MLLVaRLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLNowcastLogs_MLModelId_ComputedAt",
                table: "MLNowcastLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRegimeCAPMLogs_MLModelId_ComputedAt",
                table: "MLRegimeCAPMLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRobustCovLogs_MLModelId_ComputedAt",
                table: "MLRobustCovLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSkewRpLogs_MLModelId_ComputedAt",
                table: "MLSkewRpLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSpilloverLogs_MLModelId_ComputedAt",
                table: "MLSpilloverLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLStochVolLogs_MLModelId_ComputedAt",
                table: "MLStochVolLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLTarchLogs_MLModelId_ComputedAt",
                table: "MLTarchLogs",
                columns: new[] { "MLModelId", "ComputedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLCarrLogs");

            migrationBuilder.DropTable(
                name: "MLCarryDecompLogs");

            migrationBuilder.DropTable(
                name: "MLCipLogs");

            migrationBuilder.DropTable(
                name: "MLCornishFisherLogs");

            migrationBuilder.DropTable(
                name: "MLCorwinSchultzLogs");

            migrationBuilder.DropTable(
                name: "MLEntropyPoolingLogs");

            migrationBuilder.DropTable(
                name: "MLEstarLogs");

            migrationBuilder.DropTable(
                name: "MLEventStudyLogs");

            migrationBuilder.DropTable(
                name: "MLFactorCopulaLogs");

            migrationBuilder.DropTable(
                name: "MLGarchEvtCopulaLogs");

            migrationBuilder.DropTable(
                name: "MLGonzaloGrangerLogs");

            migrationBuilder.DropTable(
                name: "MLHansenSkewedTLogs");

            migrationBuilder.DropTable(
                name: "MLLVaRLogs");

            migrationBuilder.DropTable(
                name: "MLNowcastLogs");

            migrationBuilder.DropTable(
                name: "MLRegimeCAPMLogs");

            migrationBuilder.DropTable(
                name: "MLRobustCovLogs");

            migrationBuilder.DropTable(
                name: "MLSkewRpLogs");

            migrationBuilder.DropTable(
                name: "MLSpilloverLogs");

            migrationBuilder.DropTable(
                name: "MLStochVolLogs");

            migrationBuilder.DropTable(
                name: "MLTarchLogs");
        }
    }
}
