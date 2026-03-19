using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations11 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLConformalMartingaleLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MartingaleValue = table.Column<double>(type: "double precision", nullable: false),
                    LogMartingaleValue = table.Column<double>(type: "double precision", nullable: false),
                    AnomalyDetected = table.Column<bool>(type: "boolean", nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    StepsProcessed = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConformalMartingaleLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDmTestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModelAId = table.Column<long>(type: "bigint", nullable: false),
                    ModelBId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DmStatistic = table.Column<double>(type: "double precision", nullable: false),
                    PValue = table.Column<double>(type: "double precision", nullable: false),
                    ModelAIsSuperior = table.Column<bool>(type: "boolean", nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDmTestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHamiltonRegimeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProbRegime0 = table.Column<double>(type: "double precision", nullable: false),
                    ProbRegime1 = table.Column<double>(type: "double precision", nullable: false),
                    Transition00 = table.Column<double>(type: "double precision", nullable: false),
                    Transition11 = table.Column<double>(type: "double precision", nullable: false),
                    DominantRegime = table.Column<int>(type: "integer", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHamiltonRegimeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHrpAllocationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HrpWeight = table.Column<double>(type: "double precision", nullable: false),
                    InverseVarianceWeight = table.Column<double>(type: "double precision", nullable: false),
                    ClusterAssignment = table.Column<int>(type: "integer", nullable: false),
                    AllocationJson = table.Column<string>(type: "text", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHrpAllocationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLIsolationForestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MeanAnomalyScore = table.Column<double>(type: "double precision", nullable: false),
                    AnomalyThreshold = table.Column<double>(type: "double precision", nullable: false),
                    AnomalyCount = table.Column<int>(type: "integer", nullable: false),
                    TotalSamples = table.Column<int>(type: "integer", nullable: false),
                    NumTrees = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLIsolationForestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMcsLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    McsPValue = table.Column<double>(type: "double precision", nullable: false),
                    InConfidenceSet = table.Column<bool>(type: "boolean", nullable: false),
                    ModelRank = table.Column<int>(type: "integer", nullable: false),
                    BootstrapReplications = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMcsLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNBeatsDecompLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrendAmplitude = table.Column<double>(type: "double precision", nullable: false),
                    SeasonalAmplitude = table.Column<double>(type: "double precision", nullable: false),
                    ResidualVariance = table.Column<double>(type: "double precision", nullable: false),
                    TrendSlope = table.Column<double>(type: "double precision", nullable: false),
                    FourierCoefficientsJson = table.Column<string>(type: "text", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNBeatsDecompLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLQueryByCommitteeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SampleIndex = table.Column<int>(type: "integer", nullable: false),
                    DisagreementScore = table.Column<double>(type: "double precision", nullable: false),
                    IsHighlyInformative = table.Column<bool>(type: "boolean", nullable: false),
                    CommitteeSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLQueryByCommitteeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSprtLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LogLikelihoodRatio = table.Column<double>(type: "double precision", nullable: false),
                    LowerBound = table.Column<double>(type: "double precision", nullable: false),
                    UpperBound = table.Column<double>(type: "double precision", nullable: false),
                    StoppedLow = table.Column<bool>(type: "boolean", nullable: false),
                    StoppedHigh = table.Column<bool>(type: "boolean", nullable: false),
                    SamplesConsumed = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSprtLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLVpinLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Vpin = table.Column<double>(type: "double precision", nullable: false),
                    BuyImbalance = table.Column<double>(type: "double precision", nullable: false),
                    BucketCount = table.Column<int>(type: "integer", nullable: false),
                    ToxicityAlert = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLVpinLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalMartingaleLogs_MLModelId",
                table: "MLConformalMartingaleLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDmTestLogs_ModelAId",
                table: "MLDmTestLogs",
                column: "ModelAId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDmTestLogs_ModelBId",
                table: "MLDmTestLogs",
                column: "ModelBId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHamiltonRegimeLogs_MLModelId",
                table: "MLHamiltonRegimeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHrpAllocationLogs_MLModelId",
                table: "MLHrpAllocationLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLIsolationForestLogs_MLModelId",
                table: "MLIsolationForestLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMcsLogs_MLModelId",
                table: "MLMcsLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLNBeatsDecompLogs_MLModelId",
                table: "MLNBeatsDecompLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLQueryByCommitteeLogs_MLModelId",
                table: "MLQueryByCommitteeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSprtLogs_MLModelId",
                table: "MLSprtLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLVpinLogs_MLModelId",
                table: "MLVpinLogs",
                column: "MLModelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLConformalMartingaleLogs");

            migrationBuilder.DropTable(
                name: "MLDmTestLogs");

            migrationBuilder.DropTable(
                name: "MLHamiltonRegimeLogs");

            migrationBuilder.DropTable(
                name: "MLHrpAllocationLogs");

            migrationBuilder.DropTable(
                name: "MLIsolationForestLogs");

            migrationBuilder.DropTable(
                name: "MLMcsLogs");

            migrationBuilder.DropTable(
                name: "MLNBeatsDecompLogs");

            migrationBuilder.DropTable(
                name: "MLQueryByCommitteeLogs");

            migrationBuilder.DropTable(
                name: "MLSprtLogs");

            migrationBuilder.DropTable(
                name: "MLVpinLogs");
        }
    }
}
