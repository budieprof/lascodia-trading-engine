using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations21 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLAcdDurationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpectedDuration = table.Column<double>(type: "double precision", nullable: false),
                    AlphaParam = table.Column<double>(type: "double precision", nullable: false),
                    BetaParam = table.Column<double>(type: "double precision", nullable: false),
                    OmegaParam = table.Column<double>(type: "double precision", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAcdDurationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLArchLmTestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LmStatistic = table.Column<double>(type: "double precision", nullable: false),
                    PValue = table.Column<double>(type: "double precision", nullable: false),
                    HasRemainingArch = table.Column<bool>(type: "boolean", nullable: false),
                    LagOrder = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLArchLmTestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCrossSecMomentumLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MomentumRank = table.Column<double>(type: "double precision", nullable: false),
                    MomentumScore = table.Column<double>(type: "double precision", nullable: false),
                    RelativeReturn = table.Column<double>(type: "double precision", nullable: false),
                    IsTopQuartile = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCrossSecMomentumLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDynamicFactorModelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Factor1Loading = table.Column<double>(type: "double precision", nullable: false),
                    Factor2Loading = table.Column<double>(type: "double precision", nullable: false),
                    Factor3Loading = table.Column<double>(type: "double precision", nullable: false),
                    CommonVarianceFraction = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDynamicFactorModelLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFamaMacBethLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FactorName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RiskPremium = table.Column<double>(type: "double precision", nullable: false),
                    TStatistic = table.Column<double>(type: "double precision", nullable: false),
                    PeriodCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFamaMacBethLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGlostenMilgromLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AdverseSelectionComponent = table.Column<double>(type: "double precision", nullable: false),
                    OrderProcessingCost = table.Column<double>(type: "double precision", nullable: false),
                    InventoryCost = table.Column<double>(type: "double precision", nullable: false),
                    TotalSpread = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGlostenMilgromLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLKalmanEmLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProcessNoise = table.Column<double>(type: "double precision", nullable: false),
                    MeasurementNoise = table.Column<double>(type: "double precision", nullable: false),
                    NoiseRatio = table.Column<double>(type: "double precision", nullable: false),
                    EmIterations = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLKalmanEmLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLassoVarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SparsityRate = table.Column<double>(type: "double precision", nullable: false),
                    NetSpilloverLasso = table.Column<double>(type: "double precision", nullable: false),
                    ActiveLinks = table.Column<int>(type: "integer", nullable: false),
                    LassoLambda = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLassoVarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLeverageCycleLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LeverageIndex = table.Column<double>(type: "double precision", nullable: false),
                    LeverageChange = table.Column<double>(type: "double precision", nullable: false),
                    CrashRiskElevated = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLeverageCycleLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMarkovSwitchGarchLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Regime = table.Column<int>(type: "integer", nullable: false),
                    RegimeVolatility = table.Column<double>(type: "double precision", nullable: false),
                    TransitionProb = table.Column<double>(type: "double precision", nullable: false),
                    VolatilityForecast = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMarkovSwitchGarchLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMicropriceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Microprice = table.Column<double>(type: "double precision", nullable: false),
                    MidPrice = table.Column<double>(type: "double precision", nullable: false),
                    MicropriceBias = table.Column<double>(type: "double precision", nullable: false),
                    OrderImbalance = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMicropriceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMsvarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Regime = table.Column<int>(type: "integer", nullable: false),
                    RegimeVarCoeff = table.Column<double>(type: "double precision", nullable: false),
                    RegimeSpillover = table.Column<double>(type: "double precision", nullable: false),
                    RegimeProbability = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMsvarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPriceImpactDecayLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ImpactDecayHalfLife = table.Column<double>(type: "double precision", nullable: false),
                    InitialImpact = table.Column<double>(type: "double precision", nullable: false),
                    PersistentImpact = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPriceImpactDecayLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLQuadraticCovariationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    QuadraticCovariation = table.Column<double>(type: "double precision", nullable: false),
                    CovariationPairSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CumulativePath = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLQuadraticCovariationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRealizedEigenvolLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LargestEigenvalue = table.Column<double>(type: "double precision", nullable: false),
                    EigenConcentration = table.Column<double>(type: "double precision", nullable: false),
                    MarketModeVol = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRealizedEigenvolLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRoroIndexLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RoroScore = table.Column<double>(type: "double precision", nullable: false),
                    IsRiskOn = table.Column<bool>(type: "boolean", nullable: false),
                    VolComponent = table.Column<double>(type: "double precision", nullable: false),
                    MomentumComponent = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRoroIndexLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSemiparametricVolLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    KernelVolatility = table.Column<double>(type: "double precision", nullable: false),
                    ParametricVolatility = table.Column<double>(type: "double precision", nullable: false),
                    Bandwidth = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSemiparametricVolLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSvarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MonetaryShock = table.Column<double>(type: "double precision", nullable: false),
                    RiskShock = table.Column<double>(type: "double precision", nullable: false),
                    StructuralImpact = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSvarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTermStructVrpLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Vrp1h = table.Column<double>(type: "double precision", nullable: false),
                    Vrp4h = table.Column<double>(type: "double precision", nullable: false),
                    Vrp1d = table.Column<double>(type: "double precision", nullable: false),
                    TermSlope = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTermStructVrpLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLZumbachEffectLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ZumbachCoefficient = table.Column<double>(type: "double precision", nullable: false),
                    LongScaleRv = table.Column<double>(type: "double precision", nullable: false),
                    ShortScaleRv = table.Column<double>(type: "double precision", nullable: false),
                    AsymmetryScore = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLZumbachEffectLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLAcdDurationLogs_Symbol_ComputedAt",
                table: "MLAcdDurationLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLArchLmTestLogs_Symbol_ComputedAt",
                table: "MLArchLmTestLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCrossSecMomentumLogs_Symbol_ComputedAt",
                table: "MLCrossSecMomentumLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLDynamicFactorModelLogs_Symbol_ComputedAt",
                table: "MLDynamicFactorModelLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFamaMacBethLogs_Symbol_ComputedAt",
                table: "MLFamaMacBethLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLGlostenMilgromLogs_Symbol_ComputedAt",
                table: "MLGlostenMilgromLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLKalmanEmLogs_Symbol_ComputedAt",
                table: "MLKalmanEmLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLassoVarLogs_Symbol_ComputedAt",
                table: "MLLassoVarLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLeverageCycleLogs_Symbol_ComputedAt",
                table: "MLLeverageCycleLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMarkovSwitchGarchLogs_Symbol_ComputedAt",
                table: "MLMarkovSwitchGarchLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMicropriceLogs_Symbol_ComputedAt",
                table: "MLMicropriceLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMsvarLogs_Symbol_ComputedAt",
                table: "MLMsvarLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLPriceImpactDecayLogs_Symbol_ComputedAt",
                table: "MLPriceImpactDecayLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLQuadraticCovariationLogs_Symbol_ComputedAt",
                table: "MLQuadraticCovariationLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRealizedEigenvolLogs_Symbol_ComputedAt",
                table: "MLRealizedEigenvolLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRoroIndexLogs_Symbol_ComputedAt",
                table: "MLRoroIndexLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSemiparametricVolLogs_Symbol_ComputedAt",
                table: "MLSemiparametricVolLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSvarLogs_Symbol_ComputedAt",
                table: "MLSvarLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLTermStructVrpLogs_Symbol_ComputedAt",
                table: "MLTermStructVrpLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLZumbachEffectLogs_Symbol_ComputedAt",
                table: "MLZumbachEffectLogs",
                columns: new[] { "Symbol", "ComputedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLAcdDurationLogs");

            migrationBuilder.DropTable(
                name: "MLArchLmTestLogs");

            migrationBuilder.DropTable(
                name: "MLCrossSecMomentumLogs");

            migrationBuilder.DropTable(
                name: "MLDynamicFactorModelLogs");

            migrationBuilder.DropTable(
                name: "MLFamaMacBethLogs");

            migrationBuilder.DropTable(
                name: "MLGlostenMilgromLogs");

            migrationBuilder.DropTable(
                name: "MLKalmanEmLogs");

            migrationBuilder.DropTable(
                name: "MLLassoVarLogs");

            migrationBuilder.DropTable(
                name: "MLLeverageCycleLogs");

            migrationBuilder.DropTable(
                name: "MLMarkovSwitchGarchLogs");

            migrationBuilder.DropTable(
                name: "MLMicropriceLogs");

            migrationBuilder.DropTable(
                name: "MLMsvarLogs");

            migrationBuilder.DropTable(
                name: "MLPriceImpactDecayLogs");

            migrationBuilder.DropTable(
                name: "MLQuadraticCovariationLogs");

            migrationBuilder.DropTable(
                name: "MLRealizedEigenvolLogs");

            migrationBuilder.DropTable(
                name: "MLRoroIndexLogs");

            migrationBuilder.DropTable(
                name: "MLSemiparametricVolLogs");

            migrationBuilder.DropTable(
                name: "MLSvarLogs");

            migrationBuilder.DropTable(
                name: "MLTermStructVrpLogs");

            migrationBuilder.DropTable(
                name: "MLZumbachEffectLogs");
        }
    }
}
