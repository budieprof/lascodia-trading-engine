using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CurriculumApplied",
                table: "MLTrainingRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CvFoldScoresJson",
                table: "MLTrainingRun",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MixupApplied",
                table: "MLTrainingRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NceLossUsed",
                table: "MLTrainingRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "LatestOosMaxDrawdown",
                table: "MLModel",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PpcSurprised",
                table: "MLModel",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MLAdaptiveWindowConfig",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OptimalWindowBars = table.Column<int>(type: "integer", nullable: false),
                    BestCvLogLoss = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CvScoresJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdaptiveWindowConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLConformalBreakerLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ConsecutivePoorCoverageBars = table.Column<int>(type: "integer", nullable: false),
                    EmpiricalCoverage = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    SuspensionBars = table.Column<int>(type: "integer", nullable: false),
                    SuspendedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResumeAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConformalBreakerLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLConformalBreakerLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLCurrencyPairGraph",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Correlation = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EdgeWeight = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCurrencyPairGraph", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHStatisticLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    FeatureNameA = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FeatureNameB = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    HStatistic = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    MaterialisedAsProduct = table.Column<bool>(type: "boolean", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHStatisticLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLHStatisticLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLOosEquityCurveSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OosSharpe = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OosCalmar = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OosMaxDrawdown = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OosTotalReturn = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WindowEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AutoDemoted = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOosEquityCurveSnapshot", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLOosEquityCurveSnapshot_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLPcCausalLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CausalDagJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    MarkovBlanketFeaturesJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    MarkovBlanketSize = table.Column<int>(type: "integer", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPcCausalLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLReservoirEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ReservoirSize = table.Column<int>(type: "integer", nullable: false),
                    SpectralRadius = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    InputScaling = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ReadoutWeightsBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLReservoirEncoder", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLAdaptiveWindowConfig_Symbol_Timeframe",
                table: "MLAdaptiveWindowConfig",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalBreakerLog_MLModelId",
                table: "MLConformalBreakerLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCurrencyPairGraph_SourceSymbol_TargetSymbol_Timeframe",
                table: "MLCurrencyPairGraph",
                columns: new[] { "SourceSymbol", "TargetSymbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLHStatisticLog_MLModelId",
                table: "MLHStatisticLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOosEquityCurveSnapshot_MLModelId",
                table: "MLOosEquityCurveSnapshot",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPcCausalLog_Symbol_Timeframe",
                table: "MLPcCausalLog",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLReservoirEncoder_Symbol_Timeframe",
                table: "MLReservoirEncoder",
                columns: new[] { "Symbol", "Timeframe" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLAdaptiveWindowConfig");

            migrationBuilder.DropTable(
                name: "MLConformalBreakerLog");

            migrationBuilder.DropTable(
                name: "MLCurrencyPairGraph");

            migrationBuilder.DropTable(
                name: "MLHStatisticLog");

            migrationBuilder.DropTable(
                name: "MLOosEquityCurveSnapshot");

            migrationBuilder.DropTable(
                name: "MLPcCausalLog");

            migrationBuilder.DropTable(
                name: "MLReservoirEncoder");

            migrationBuilder.DropColumn(
                name: "CurriculumApplied",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "CvFoldScoresJson",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "MixupApplied",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "NceLossUsed",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "LatestOosMaxDrawdown",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "PpcSurprised",
                table: "MLModel");
        }
    }
}
