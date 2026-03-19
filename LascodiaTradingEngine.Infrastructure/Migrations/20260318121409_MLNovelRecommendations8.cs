using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations8 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLAdversarialRobustnessLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    Epsilon = table.Column<double>(type: "double precision", nullable: false),
                    BaseAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    PerturbedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyDrop = table.Column<double>(type: "double precision", nullable: false),
                    SamplesTested = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdversarialRobustnessLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLAdversarialRobustnessLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLAttentionWeightLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    AttentionWeightsJson = table.Column<string>(type: "text", nullable: false),
                    TopFeatureIdx = table.Column<int>(type: "integer", nullable: false),
                    TopFeatureWeight = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAttentionWeightLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLAttentionWeightLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLBocpdChangePoint",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    ChangePointAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RunLength = table.Column<int>(type: "integer", nullable: false),
                    MaxPosterior = table.Column<double>(type: "double precision", nullable: false),
                    IsConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBocpdChangePoint", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLBocpdChangePoint_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLConformalEfficiencyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    AvgSetSize = table.Column<double>(type: "double precision", nullable: false),
                    BaselineSetSize = table.Column<double>(type: "double precision", nullable: false),
                    EfficiencyRatio = table.Column<double>(type: "double precision", nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConformalEfficiencyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLConformalEfficiencyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLDirichletUncertaintyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    AvgEpistemicUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    AvgAleatoricUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    AvgConcentration = table.Column<double>(type: "double precision", nullable: false),
                    SamplesCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDirichletUncertaintyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLDirichletUncertaintyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLEconomicImpactLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    EconomicEventId = table.Column<long>(type: "bigint", nullable: true),
                    EventType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AccuracyBefore = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyAfter = table.Column<double>(type: "double precision", nullable: false),
                    DiffInDiff = table.Column<double>(type: "double precision", nullable: false),
                    SamplesCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEconomicImpactLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEconomicImpactLog_EconomicEvent_EconomicEventId",
                        column: x => x.EconomicEventId,
                        principalTable: "EconomicEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MLEconomicImpactLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLExperienceReplayEntry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeaturesJson = table.Column<string>(type: "text", nullable: false),
                    ActualDirection = table.Column<int>(type: "integer", nullable: false),
                    PredictedProb = table.Column<double>(type: "double precision", nullable: false),
                    WasCorrect = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLExperienceReplayEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLExperienceReplayEntry_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLFeatureStalenessLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Lag1Autocorr = table.Column<double>(type: "double precision", nullable: false),
                    IsStale = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFeatureStalenessLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLFeatureStalenessLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLFuzzyRegimeMembership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrendingWeight = table.Column<double>(type: "double precision", nullable: false),
                    RangingWeight = table.Column<double>(type: "double precision", nullable: false),
                    VolatileWeight = table.Column<double>(type: "double precision", nullable: false),
                    BlendedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFuzzyRegimeMembership", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLFuzzyRegimeMembership_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLGradientSaliencyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    SaliencyVectorJson = table.Column<string>(type: "text", nullable: false),
                    L2ShiftFromPrior = table.Column<double>(type: "double precision", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGradientSaliencyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLGradientSaliencyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLPcaWhiteningLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    EigenvaluesJson = table.Column<string>(type: "text", nullable: false),
                    EigenvectorsJson = table.Column<string>(type: "text", nullable: false),
                    ExplainedVarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPcaWhiteningLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLPcaWhiteningLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLRegimeFeatureImportance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    Regime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ImportanceScore = table.Column<double>(type: "double precision", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRegimeFeatureImportance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRegimeFeatureImportance_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLSessionPlattCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    Session = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PlattA = table.Column<double>(type: "double precision", nullable: false),
                    PlattB = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSessionPlattCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSessionPlattCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLShapDriftLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BaselineShap = table.Column<double>(type: "double precision", nullable: false),
                    CurrentShap = table.Column<double>(type: "double precision", nullable: false),
                    SignFlipped = table.Column<bool>(type: "boolean", nullable: false),
                    RelativeMagnitudeShift = table.Column<double>(type: "double precision", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLShapDriftLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLShapDriftLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLAdversarialRobustnessLog_MLModelId",
                table: "MLAdversarialRobustnessLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLAttentionWeightLog_MLModelId",
                table: "MLAttentionWeightLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBocpdChangePoint_MLModelId",
                table: "MLBocpdChangePoint",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalEfficiencyLog_MLModelId",
                table: "MLConformalEfficiencyLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDirichletUncertaintyLog_MLModelId",
                table: "MLDirichletUncertaintyLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEconomicImpactLog_EconomicEventId",
                table: "MLEconomicImpactLog",
                column: "EconomicEventId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEconomicImpactLog_MLModelId",
                table: "MLEconomicImpactLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLExperienceReplayEntry_MLModelId_ResolvedAt",
                table: "MLExperienceReplayEntry",
                columns: new[] { "MLModelId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureStalenessLog_MLModelId_FeatureName",
                table: "MLFeatureStalenessLog",
                columns: new[] { "MLModelId", "FeatureName" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFuzzyRegimeMembership_MLModelId",
                table: "MLFuzzyRegimeMembership",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGradientSaliencyLog_MLModelId",
                table: "MLGradientSaliencyLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPcaWhiteningLog_MLModelId",
                table: "MLPcaWhiteningLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRegimeFeatureImportance_MLModelId_Regime",
                table: "MLRegimeFeatureImportance",
                columns: new[] { "MLModelId", "Regime" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSessionPlattCalibration_MLModelId_Session",
                table: "MLSessionPlattCalibration",
                columns: new[] { "MLModelId", "Session" });

            migrationBuilder.CreateIndex(
                name: "IX_MLShapDriftLog_MLModelId",
                table: "MLShapDriftLog",
                column: "MLModelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLAdversarialRobustnessLog");

            migrationBuilder.DropTable(
                name: "MLAttentionWeightLog");

            migrationBuilder.DropTable(
                name: "MLBocpdChangePoint");

            migrationBuilder.DropTable(
                name: "MLConformalEfficiencyLog");

            migrationBuilder.DropTable(
                name: "MLDirichletUncertaintyLog");

            migrationBuilder.DropTable(
                name: "MLEconomicImpactLog");

            migrationBuilder.DropTable(
                name: "MLExperienceReplayEntry");

            migrationBuilder.DropTable(
                name: "MLFeatureStalenessLog");

            migrationBuilder.DropTable(
                name: "MLFuzzyRegimeMembership");

            migrationBuilder.DropTable(
                name: "MLGradientSaliencyLog");

            migrationBuilder.DropTable(
                name: "MLPcaWhiteningLog");

            migrationBuilder.DropTable(
                name: "MLRegimeFeatureImportance");

            migrationBuilder.DropTable(
                name: "MLSessionPlattCalibration");

            migrationBuilder.DropTable(
                name: "MLShapDriftLog");
        }
    }
}
