using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations12 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLDbscanClusterLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ClusterCount = table.Column<int>(type: "integer", nullable: false),
                    NoisePointCount = table.Column<int>(type: "integer", nullable: false),
                    Epsilon = table.Column<double>(type: "double precision", nullable: false),
                    MinPoints = table.Column<int>(type: "integer", nullable: false),
                    ClusterSizesJson = table.Column<string>(type: "text", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDbscanClusterLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHuberRegressionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HuberDelta = table.Column<double>(type: "double precision", nullable: false),
                    RmseRobust = table.Column<double>(type: "double precision", nullable: false),
                    RmseOls = table.Column<double>(type: "double precision", nullable: false),
                    ImprovementRatio = table.Column<double>(type: "double precision", nullable: false),
                    OutlierCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHuberRegressionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLKellyFractionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    KellyFraction = table.Column<double>(type: "double precision", nullable: false),
                    HalfKelly = table.Column<double>(type: "double precision", nullable: false),
                    WinRate = table.Column<double>(type: "double precision", nullable: false),
                    WinLossRatio = table.Column<double>(type: "double precision", nullable: false),
                    NegativeEV = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLKellyFractionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMembershipInferenceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrainConfidenceMean = table.Column<double>(type: "double precision", nullable: false),
                    TestConfidenceMean = table.Column<double>(type: "double precision", nullable: false),
                    ConfidenceGap = table.Column<double>(type: "double precision", nullable: false),
                    AttackAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    VulnerabilityAlert = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMembershipInferenceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLOhlcVolatilityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ParkinsonVol = table.Column<double>(type: "double precision", nullable: false),
                    GarmanKlassVol = table.Column<double>(type: "double precision", nullable: false),
                    YangZhangVol = table.Column<double>(type: "double precision", nullable: false),
                    CloseToCloseVol = table.Column<double>(type: "double precision", nullable: false),
                    ConsensusVol = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOhlcVolatilityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLOmegaCalmarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OmegaRatio = table.Column<double>(type: "double precision", nullable: false),
                    CalmarRatio = table.Column<double>(type: "double precision", nullable: false),
                    MaxDrawdown = table.Column<double>(type: "double precision", nullable: false),
                    AnnualisedReturn = table.Column<double>(type: "double precision", nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOmegaCalmarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPeltChangePointLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChangePointCount = table.Column<int>(type: "integer", nullable: false),
                    ChangePointIndicesJson = table.Column<string>(type: "text", nullable: false),
                    Penalty = table.Column<double>(type: "double precision", nullable: false),
                    TotalCost = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPeltChangePointLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSsaComponentLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WindowLength = table.Column<int>(type: "integer", nullable: false),
                    TrendVarianceExplained = table.Column<double>(type: "double precision", nullable: false),
                    OscillatoryVarianceExplained = table.Column<double>(type: "double precision", nullable: false),
                    NoiseVarianceExplained = table.Column<double>(type: "double precision", nullable: false),
                    TrendComponentCount = table.Column<int>(type: "integer", nullable: false),
                    OscillatoryComponentCount = table.Column<int>(type: "integer", nullable: false),
                    SingularValuesJson = table.Column<string>(type: "text", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSsaComponentLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTaskArithmeticLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaseModelId = table.Column<long>(type: "bigint", nullable: false),
                    TaskModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TaskVectorNorm = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyBeforeArithmetic = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyAfterAddition = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyAfterNegation = table.Column<double>(type: "double precision", nullable: false),
                    TaskDescription = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTaskArithmeticLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTransferEntropyLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TransferEntropyXtoY = table.Column<double>(type: "double precision", nullable: false),
                    TransferEntropyYtoX = table.Column<double>(type: "double precision", nullable: false),
                    NetInformationFlow = table.Column<double>(type: "double precision", nullable: false),
                    XDrivesY = table.Column<bool>(type: "boolean", nullable: false),
                    Lag = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTransferEntropyLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLDbscanClusterLogs_MLModelId",
                table: "MLDbscanClusterLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHuberRegressionLogs_MLModelId",
                table: "MLHuberRegressionLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLKellyFractionLogs_MLModelId",
                table: "MLKellyFractionLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMembershipInferenceLogs_MLModelId",
                table: "MLMembershipInferenceLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOhlcVolatilityLogs_MLModelId",
                table: "MLOhlcVolatilityLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOmegaCalmarLogs_MLModelId",
                table: "MLOmegaCalmarLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPeltChangePointLogs_MLModelId",
                table: "MLPeltChangePointLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSsaComponentLogs_MLModelId",
                table: "MLSsaComponentLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTaskArithmeticLogs_BaseModelId",
                table: "MLTaskArithmeticLogs",
                column: "BaseModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTaskArithmeticLogs_TaskModelId",
                table: "MLTaskArithmeticLogs",
                column: "TaskModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTransferEntropyLogs_SourceSymbol",
                table: "MLTransferEntropyLogs",
                column: "SourceSymbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLTransferEntropyLogs_TargetSymbol",
                table: "MLTransferEntropyLogs",
                column: "TargetSymbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLDbscanClusterLogs");

            migrationBuilder.DropTable(
                name: "MLHuberRegressionLogs");

            migrationBuilder.DropTable(
                name: "MLKellyFractionLogs");

            migrationBuilder.DropTable(
                name: "MLMembershipInferenceLogs");

            migrationBuilder.DropTable(
                name: "MLOhlcVolatilityLogs");

            migrationBuilder.DropTable(
                name: "MLOmegaCalmarLogs");

            migrationBuilder.DropTable(
                name: "MLPeltChangePointLogs");

            migrationBuilder.DropTable(
                name: "MLSsaComponentLogs");

            migrationBuilder.DropTable(
                name: "MLTaskArithmeticLogs");

            migrationBuilder.DropTable(
                name: "MLTransferEntropyLogs");
        }
    }
}
