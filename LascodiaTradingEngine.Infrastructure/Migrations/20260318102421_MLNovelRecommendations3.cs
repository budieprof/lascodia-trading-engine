using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CoresetSelectionRatio",
                table: "MLTrainingRun",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RareEventWeightingApplied",
                table: "MLTrainingRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "EstimatedTimeToTargetBars",
                table: "MLModelPredictionLog",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SurvivalHazardRate",
                table: "MLModelPredictionLog",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSoupModel",
                table: "MLModel",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "PlattCalibrationDrift",
                table: "MLModel",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MLAdversarialValidationLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    AdvAuroc = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    TrainSamples = table.Column<int>(type: "integer", nullable: false),
                    TestSamples = table.Column<int>(type: "integer", nullable: false),
                    TopFeaturesJson = table.Column<string>(type: "text", nullable: false),
                    ShiftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdversarialValidationLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCpcEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    EmbeddingDim = table.Column<int>(type: "integer", nullable: false),
                    PredictionSteps = table.Column<int>(type: "integer", nullable: false),
                    InfoNceLoss = table.Column<double>(type: "double precision", precision: 12, scale: 6, nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    EncoderBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCpcEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEtsParams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    Beta = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    ValidationMse = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    FitSamples = table.Column<int>(type: "integer", nullable: false),
                    FittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEtsParams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGarchModel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    Omega = table.Column<double>(type: "double precision", precision: 18, scale: 12, nullable: false),
                    Alpha = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    Beta = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", precision: 18, scale: 6, nullable: false),
                    FitSamples = table.Column<int>(type: "integer", nullable: false),
                    FittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGarchModel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMrmrFeatureRanking",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MrmrRank = table.Column<int>(type: "integer", nullable: false),
                    MutualInfoWithTarget = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    RedundancyScore = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    MrmrScore = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMrmrFeatureRanking", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPartialDependenceBaseline",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GridValuesJson = table.Column<string>(type: "text", nullable: false),
                    BaselinePdpJson = table.Column<string>(type: "text", nullable: false),
                    CurrentPdpJson = table.Column<string>(type: "text", nullable: true),
                    MaxDeviationFromBaseline = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: true),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    BaselineComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPartialDependenceBaseline", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLPartialDependenceBaseline_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLQuantileCoverageLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    EmpiricalPicp = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    MeanIntervalWidthPips = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: false),
                    WinklerScore = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WindowEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLQuantileCoverageLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLQuantileCoverageLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLSurvivalModel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    CoefficientsJson = table.Column<string>(type: "text", nullable: false),
                    ConcordanceIndex = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    BaselineHazardJson = table.Column<string>(type: "text", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSurvivalModel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLVaeEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    LatentDim = table.Column<int>(type: "integer", nullable: false),
                    InputDim = table.Column<int>(type: "integer", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    ReconstructionLoss = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EncoderBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLVaeEncoder", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLAdversarialValidationLog_Symbol_Timeframe_ComputedAt",
                table: "MLAdversarialValidationLog",
                columns: new[] { "Symbol", "Timeframe", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_IsActive",
                table: "MLCpcEncoder",
                columns: new[] { "Symbol", "Timeframe", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEtsParams_Symbol_Timeframe_FittedAt",
                table: "MLEtsParams",
                columns: new[] { "Symbol", "Timeframe", "FittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLGarchModel_Symbol_Timeframe_FittedAt",
                table: "MLGarchModel",
                columns: new[] { "Symbol", "Timeframe", "FittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMrmrFeatureRanking_Symbol_Timeframe_ComputedAt",
                table: "MLMrmrFeatureRanking",
                columns: new[] { "Symbol", "Timeframe", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMrmrFeatureRanking_Symbol_Timeframe_MrmrRank",
                table: "MLMrmrFeatureRanking",
                columns: new[] { "Symbol", "Timeframe", "MrmrRank" });

            migrationBuilder.CreateIndex(
                name: "IX_MLPartialDependenceBaseline_MLModelId_FeatureName",
                table: "MLPartialDependenceBaseline",
                columns: new[] { "MLModelId", "FeatureName" });

            migrationBuilder.CreateIndex(
                name: "IX_MLPartialDependenceBaseline_Symbol_Timeframe_FeatureName",
                table: "MLPartialDependenceBaseline",
                columns: new[] { "Symbol", "Timeframe", "FeatureName" });

            migrationBuilder.CreateIndex(
                name: "IX_MLQuantileCoverageLog_MLModelId_ComputedAt",
                table: "MLQuantileCoverageLog",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLQuantileCoverageLog_Symbol_Timeframe_WindowEnd",
                table: "MLQuantileCoverageLog",
                columns: new[] { "Symbol", "Timeframe", "WindowEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSurvivalModel_Symbol_Timeframe_IsActive",
                table: "MLSurvivalModel",
                columns: new[] { "Symbol", "Timeframe", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_MLVaeEncoder_Symbol_Timeframe_IsActive",
                table: "MLVaeEncoder",
                columns: new[] { "Symbol", "Timeframe", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLAdversarialValidationLog");

            migrationBuilder.DropTable(
                name: "MLCpcEncoder");

            migrationBuilder.DropTable(
                name: "MLEtsParams");

            migrationBuilder.DropTable(
                name: "MLGarchModel");

            migrationBuilder.DropTable(
                name: "MLMrmrFeatureRanking");

            migrationBuilder.DropTable(
                name: "MLPartialDependenceBaseline");

            migrationBuilder.DropTable(
                name: "MLQuantileCoverageLog");

            migrationBuilder.DropTable(
                name: "MLSurvivalModel");

            migrationBuilder.DropTable(
                name: "MLVaeEncoder");

            migrationBuilder.DropColumn(
                name: "CoresetSelectionRatio",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "RareEventWeightingApplied",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "EstimatedTimeToTargetBars",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "SurvivalHazardRate",
                table: "MLModelPredictionLog");

            migrationBuilder.DropColumn(
                name: "IsSoupModel",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "PlattCalibrationDrift",
                table: "MLModel");
        }
    }
}
