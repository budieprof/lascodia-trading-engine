using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations18 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLAlphaStableLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AlphaIndex = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    BetaSkewness = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GammaScale = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DeltaLocation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    EstimationMethod = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TailVaR99 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GoodnessFitKs = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAlphaStableLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDynotearsLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ContemporaneousEdgesJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    LaggedEdgesJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    MaxLagOrder = table.Column<int>(type: "integer", nullable: false),
                    AcyclicityResidual = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SparsityLevel = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ConvergedSuccessfully = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDynotearsLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEigenportfolioLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Top3EigenvalueShareJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    FiedlerPortfolioJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    MarketBetaExposure = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    AlphaExplainedVar = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IdiosyncraticRisk = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    PairCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEigenportfolioLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPcmciLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CausalGraphJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SignificantEdgeCount = table.Column<int>(type: "integer", nullable: false),
                    MaxLagTested = table.Column<int>(type: "integer", nullable: false),
                    FalsePositiveRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MciTestCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPcmciLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRoughVolLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    HurstExponent = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RoughVol = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ClassicalVol = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RoughnessDifference = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    BergomiXi = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRoughVolLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSsaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    EigenvalueShareJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TrendReconstructionError = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SignalToNoiseRatio = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    LagWindowSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSsaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLVmdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ModeCount = table.Column<int>(type: "integer", nullable: false),
                    ModeCenterFrequenciesJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ModeEnergiesJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ReconstructionError = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ConvergenceIterations = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLVmdLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLAlphaStableLogs_Symbol_ComputedAt",
                table: "MLAlphaStableLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLDynotearsLogs_Symbol_ComputedAt",
                table: "MLDynotearsLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEigenportfolioLogs_Symbol_ComputedAt",
                table: "MLEigenportfolioLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLPcmciLogs_Symbol_ComputedAt",
                table: "MLPcmciLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRoughVolLogs_MLModelId_ComputedAt",
                table: "MLRoughVolLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSsaLogs_Symbol_ComputedAt",
                table: "MLSsaLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLVmdLogs_Symbol_ComputedAt",
                table: "MLVmdLogs",
                columns: new[] { "Symbol", "ComputedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLAlphaStableLogs");

            migrationBuilder.DropTable(
                name: "MLDynotearsLogs");

            migrationBuilder.DropTable(
                name: "MLEigenportfolioLogs");

            migrationBuilder.DropTable(
                name: "MLPcmciLogs");

            migrationBuilder.DropTable(
                name: "MLRoughVolLogs");

            migrationBuilder.DropTable(
                name: "MLSsaLogs");

            migrationBuilder.DropTable(
                name: "MLVmdLogs");
        }
    }
}
