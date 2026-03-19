using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CurriculumFinalDifficulty",
                table: "MLTrainingRun",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDistillationRun",
                table: "MLTrainingRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsEmergencyRetrain",
                table: "MLTrainingRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPretrainingRun",
                table: "MLTrainingRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LearnerArchitecture",
                table: "MLTrainingRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CounterfactualJson",
                table: "MLModelPredictionLog",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LatencyMs",
                table: "MLModelPredictionLog",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MagnitudeUncertaintyPips",
                table: "MLModelPredictionLog",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "McDropoutMean",
                table: "MLModelPredictionLog",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "McDropoutVariance",
                table: "MLModelPredictionLog",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DistilledFromModelId",
                table: "MLModel",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDistilled",
                table: "MLModel",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LearnerArchitecture",
                table: "MLModel",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "TransferredFromModelId",
                table: "MLModel",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MLCausalFeatureAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FeatureIndex = table.Column<int>(type: "integer", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GrangerFStat = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    GrangerPValue = table.Column<decimal>(type: "numeric(10,8)", precision: 10, scale: 8, nullable: false),
                    LagOrder = table.Column<int>(type: "integer", nullable: false),
                    IsCausal = table.Column<bool>(type: "boolean", nullable: false),
                    IsMaskedForTraining = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCausalFeatureAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCausalFeatureAudit_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLEnsembleLearnerWeight",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    LearnerIndex = table.Column<int>(type: "integer", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(10,8)", precision: 10, scale: 8, nullable: false),
                    CumulativeLogWealth = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    RollingBrierScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    RollingPredictions = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEnsembleLearnerWeight", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEnsembleLearnerWeight_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLLiquidityRegimeAlert",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    UtcHour = table.Column<int>(type: "integer", nullable: false),
                    SpreadPips = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    SpreadPercentileRank = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    RollingMedianSpread = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    IsAnomalous = table.Column<bool>(type: "boolean", nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLiquidityRegimeAlert", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLModelEwmaAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    EwmaAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    LastPredictionAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelEwmaAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelEwmaAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLModelHorizonAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HorizonBars = table.Column<int>(type: "integer", nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelHorizonAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelHorizonAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLModelHourlyAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HourUtc = table.Column<int>(type: "integer", nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelHourlyAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelHourlyAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLModelMagnitudeStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    MeanAbsoluteError = table.Column<double>(type: "double precision", nullable: false),
                    CorrelationCoefficient = table.Column<double>(type: "double precision", nullable: false),
                    MeanSignedBias = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelMagnitudeStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelMagnitudeStats_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLModelVolatilityAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    VolatilityBucket = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    AtrThresholdLow = table.Column<decimal>(type: "numeric", nullable: false),
                    AtrThresholdHigh = table.Column<decimal>(type: "numeric", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelVolatilityAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelVolatilityAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLOptimizationParetoFront",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SearchBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    MLTrainingRunId = table.Column<long>(type: "bigint", nullable: false),
                    ObjectiveAccuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    ObjectiveSharpe = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    ObjectiveStability = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    HypervolumeContrib = table.Column<decimal>(type: "numeric(10,8)", precision: 10, scale: 8, nullable: false),
                    ParetoRank = table.Column<int>(type: "integer", nullable: false),
                    IsDeploymentCandidate = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: true),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOptimizationParetoFront", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLOptimizationParetoFront_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MLOptimizationParetoFront_MLTrainingRun_MLTrainingRunId",
                        column: x => x.MLTrainingRunId,
                        principalTable: "MLTrainingRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLModel_DistilledFromModelId",
                table: "MLModel",
                column: "DistilledFromModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLModel_TransferredFromModelId",
                table: "MLModel",
                column: "TransferredFromModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCausalFeatureAudit_MLModelId_FeatureIndex",
                table: "MLCausalFeatureAudit",
                columns: new[] { "MLModelId", "FeatureIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCausalFeatureAudit_MLModelId_IsCausal",
                table: "MLCausalFeatureAudit",
                columns: new[] { "MLModelId", "IsCausal" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEnsembleLearnerWeight_MLModelId_LearnerIndex",
                table: "MLEnsembleLearnerWeight",
                columns: new[] { "MLModelId", "LearnerIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLLiquidityRegimeAlert_Symbol_IsAnomalous_ResolvedAt",
                table: "MLLiquidityRegimeAlert",
                columns: new[] { "Symbol", "IsAnomalous", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLiquidityRegimeAlert_Symbol_TriggeredAt",
                table: "MLLiquidityRegimeAlert",
                columns: new[] { "Symbol", "TriggeredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelEwmaAccuracy_MLModelId",
                table: "MLModelEwmaAccuracy",
                column: "MLModelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelEwmaAccuracy_Symbol_Timeframe",
                table: "MLModelEwmaAccuracy",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHorizonAccuracy_MLModelId_HorizonBars",
                table: "MLModelHorizonAccuracy",
                columns: new[] { "MLModelId", "HorizonBars" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHorizonAccuracy_Symbol_Timeframe_HorizonBars",
                table: "MLModelHorizonAccuracy",
                columns: new[] { "Symbol", "Timeframe", "HorizonBars" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHourlyAccuracy_MLModelId_HourUtc",
                table: "MLModelHourlyAccuracy",
                columns: new[] { "MLModelId", "HourUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHourlyAccuracy_Symbol_Timeframe_HourUtc",
                table: "MLModelHourlyAccuracy",
                columns: new[] { "Symbol", "Timeframe", "HourUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelMagnitudeStats_MLModelId",
                table: "MLModelMagnitudeStats",
                column: "MLModelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelMagnitudeStats_Symbol_Timeframe",
                table: "MLModelMagnitudeStats",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelVolatilityAccuracy_MLModelId_VolatilityBucket",
                table: "MLModelVolatilityAccuracy",
                columns: new[] { "MLModelId", "VolatilityBucket" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelVolatilityAccuracy_Symbol_Timeframe_VolatilityBucket",
                table: "MLModelVolatilityAccuracy",
                columns: new[] { "Symbol", "Timeframe", "VolatilityBucket" });

            migrationBuilder.CreateIndex(
                name: "IX_MLOptimizationParetoFront_MLModelId",
                table: "MLOptimizationParetoFront",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOptimizationParetoFront_MLTrainingRunId",
                table: "MLOptimizationParetoFront",
                column: "MLTrainingRunId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOptimizationParetoFront_SearchBatchId_ParetoRank",
                table: "MLOptimizationParetoFront",
                columns: new[] { "SearchBatchId", "ParetoRank" });

            migrationBuilder.CreateIndex(
                name: "IX_MLOptimizationParetoFront_Symbol_Timeframe_IsDeploymentCand~",
                table: "MLOptimizationParetoFront",
                columns: new[] { "Symbol", "Timeframe", "IsDeploymentCandidate" });

            migrationBuilder.AddForeignKey(
                name: "FK_MLModel_MLModel_DistilledFromModelId",
                table: "MLModel",
                column: "DistilledFromModelId",
                principalTable: "MLModel",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MLModel_MLModel_TransferredFromModelId",
                table: "MLModel",
                column: "TransferredFromModelId",
                principalTable: "MLModel",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MLModel_MLModel_DistilledFromModelId",
                table: "MLModel");

            migrationBuilder.DropForeignKey(
                name: "FK_MLModel_MLModel_TransferredFromModelId",
                table: "MLModel");

            migrationBuilder.DropTable(
                name: "MLCausalFeatureAudit");

            migrationBuilder.DropTable(
                name: "MLEnsembleLearnerWeight");

            migrationBuilder.DropTable(
                name: "MLLiquidityRegimeAlert");

            migrationBuilder.DropTable(
                name: "MLModelEwmaAccuracy");

            migrationBuilder.DropTable(
                name: "MLModelHorizonAccuracy");

            migrationBuilder.DropTable(
                name: "MLModelHourlyAccuracy");

            migrationBuilder.DropTable(
                name: "MLModelMagnitudeStats");

            migrationBuilder.DropTable(
                name: "MLModelVolatilityAccuracy");

            migrationBuilder.DropTable(
                name: "MLOptimizationParetoFront");

            migrationBuilder.DropIndex(
                name: "IX_MLModel_DistilledFromModelId",
                table: "MLModel");

            migrationBuilder.DropIndex(
                name: "IX_MLModel_TransferredFromModelId",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "CurriculumFinalDifficulty",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "IsDistillationRun",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "IsEmergencyRetrain",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "IsPretrainingRun",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "LearnerArchitecture",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "CounterfactualJson",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "LatencyMs",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "MagnitudeUncertaintyPips",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "McDropoutMean",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "McDropoutVariance",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "DistilledFromModelId",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "IsDistilled",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "LearnerArchitecture",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "TransferredFromModelId",
                table: "MLModel");
        }
    }
}
