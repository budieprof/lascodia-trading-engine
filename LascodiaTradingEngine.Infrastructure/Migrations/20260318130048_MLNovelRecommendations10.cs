using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations10 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLActComplexityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActComplexityScore = table.Column<double>(type: "double precision", nullable: false),
                    NormalizedComplexity = table.Column<double>(type: "double precision", nullable: false),
                    CompressedSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    OriginalSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLActComplexityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAdaptiveDropoutLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LayerDropoutRatesJson = table.Column<string>(type: "text", nullable: false),
                    ValidationLoss = table.Column<double>(type: "double precision", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    EpochNumber = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdaptiveDropoutLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBayesFactorLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HypothesisLabel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LogBayesFactor = table.Column<double>(type: "double precision", nullable: false),
                    Evidence = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBayesFactorLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDataCartographyLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SampleIndex = table.Column<int>(type: "integer", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Variability = table.Column<double>(type: "double precision", nullable: false),
                    Cartography = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDataCartographyLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFederatedModelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RoundNumber = table.Column<int>(type: "integer", nullable: false),
                    ClientCount = table.Column<int>(type: "integer", nullable: false),
                    AggregatedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ClientWeightsJson = table.Column<string>(type: "text", nullable: true),
                    ConvergenceReached = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFederatedModelLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPerformanceDecompositionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SkillComponent = table.Column<double>(type: "double precision", nullable: false),
                    LuckComponent = table.Column<double>(type: "double precision", nullable: false),
                    BiasComponent = table.Column<double>(type: "double precision", nullable: false),
                    TotalAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPerformanceDecompositionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLProfitFactorLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProfitFactor = table.Column<double>(type: "double precision", nullable: false),
                    GrossProfit = table.Column<double>(type: "double precision", nullable: false),
                    GrossLoss = table.Column<double>(type: "double precision", nullable: false),
                    WinCount = table.Column<int>(type: "integer", nullable: false),
                    LossCount = table.Column<int>(type: "integer", nullable: false),
                    AverageWin = table.Column<double>(type: "double precision", nullable: false),
                    AverageLoss = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLProfitFactorLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRetrainingScheduleLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TriggerReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DriftScore = table.Column<double>(type: "double precision", nullable: false),
                    PerformanceDelta = table.Column<double>(type: "double precision", nullable: false),
                    RetrainingTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRetrainingScheduleLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSlicedWassersteinLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SlicedWassersteinDistance = table.Column<double>(type: "double precision", nullable: false),
                    NumProjections = table.Column<int>(type: "integer", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSlicedWassersteinLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTtaInferenceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AugmentationCount = table.Column<int>(type: "integer", nullable: false),
                    OriginalProbability = table.Column<double>(type: "double precision", nullable: false),
                    AveragedProbability = table.Column<double>(type: "double precision", nullable: false),
                    VarianceAcrossAugmentations = table.Column<double>(type: "double precision", nullable: false),
                    DirectionFlipped = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTtaInferenceLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLActComplexityLogs_MLModelId",
                table: "MLActComplexityLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLAdaptiveDropoutLogs_MLModelId",
                table: "MLAdaptiveDropoutLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBayesFactorLogs_MLModelId",
                table: "MLBayesFactorLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDataCartographyLogs_MLModelId",
                table: "MLDataCartographyLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLFederatedModelLogs_MLModelId",
                table: "MLFederatedModelLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPerformanceDecompositionLogs_MLModelId",
                table: "MLPerformanceDecompositionLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLProfitFactorLogs_MLModelId",
                table: "MLProfitFactorLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRetrainingScheduleLogs_MLModelId",
                table: "MLRetrainingScheduleLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSlicedWassersteinLogs_MLModelId",
                table: "MLSlicedWassersteinLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTtaInferenceLogs_MLModelId",
                table: "MLTtaInferenceLogs",
                column: "MLModelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLActComplexityLogs");

            migrationBuilder.DropTable(
                name: "MLAdaptiveDropoutLogs");

            migrationBuilder.DropTable(
                name: "MLBayesFactorLogs");

            migrationBuilder.DropTable(
                name: "MLDataCartographyLogs");

            migrationBuilder.DropTable(
                name: "MLFederatedModelLogs");

            migrationBuilder.DropTable(
                name: "MLPerformanceDecompositionLogs");

            migrationBuilder.DropTable(
                name: "MLProfitFactorLogs");

            migrationBuilder.DropTable(
                name: "MLRetrainingScheduleLogs");

            migrationBuilder.DropTable(
                name: "MLSlicedWassersteinLogs");

            migrationBuilder.DropTable(
                name: "MLTtaInferenceLogs");
        }
    }
}
