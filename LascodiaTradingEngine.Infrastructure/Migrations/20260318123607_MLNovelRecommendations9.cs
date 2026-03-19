using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations9 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLCandleBertEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    EncoderWeightsJson = table.Column<string>(type: "text", nullable: false),
                    NumLayers = table.Column<int>(type: "integer", nullable: false),
                    HiddenDim = table.Column<int>(type: "integer", nullable: false),
                    TrainSamples = table.Column<int>(type: "integer", nullable: false),
                    ReconstructionLoss = table.Column<double>(type: "double precision", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCandleBertEncoder", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCandleBertEncoder_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLCmimFeatureRankingLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ConditionalMi = table.Column<double>(type: "double precision", nullable: false),
                    SelectionRank = table.Column<int>(type: "integer", nullable: false),
                    IsSelected = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCmimFeatureRankingLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCmimFeatureRankingLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLConformalAnomalyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    ConformalPValue = table.Column<double>(type: "double precision", nullable: false),
                    IsAnomaly = table.Column<bool>(type: "boolean", nullable: false),
                    FeaturesJson = table.Column<string>(type: "text", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConformalAnomalyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLConformalAnomalyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLInputGradientNormLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    GradientNormsJson = table.Column<string>(type: "text", nullable: false),
                    MaxGradientNorm = table.Column<double>(type: "double precision", nullable: false),
                    HighGradientFeatureIdx = table.Column<int>(type: "integer", nullable: false),
                    PenaltyTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLInputGradientNormLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLInputGradientNormLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLKalmanCoefficientLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PosteriorMean = table.Column<double>(type: "double precision", nullable: false),
                    PosteriorVariance = table.Column<double>(type: "double precision", nullable: false),
                    KalmanGain = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLKalmanCoefficientLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLKalmanCoefficientLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLLotteryTicketLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    PruningRound = table.Column<int>(type: "integer", nullable: false),
                    SparsityRatio = table.Column<double>(type: "double precision", nullable: false),
                    PrunedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    BaseAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyRetention = table.Column<double>(type: "double precision", nullable: false),
                    IsWinningTicket = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLotteryTicketLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLLotteryTicketLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLRegimeSynchronyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PrimarySymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SynchronisedPairCount = table.Column<int>(type: "integer", nullable: false),
                    SynchronisedSymbolsJson = table.Column<string>(type: "text", nullable: false),
                    Regime = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SynchronyScore = table.Column<double>(type: "double precision", nullable: false),
                    IsSystemicRisk = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRegimeSynchronyLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRetrogradeFalsePatternLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FailureMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MeanFeaturesJson = table.Column<string>(type: "text", nullable: false),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    TopDivergentFeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TopDivergentDelta = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRetrogradeFalsePatternLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRetrogradeFalsePatternLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLRpcaAnomalyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    MeanSparseNorm = table.Column<double>(type: "double precision", nullable: false),
                    MaxSparseNorm = table.Column<double>(type: "double precision", nullable: false),
                    AnomalousSampleCount = table.Column<int>(type: "integer", nullable: false),
                    AnomalyThreshold = table.Column<double>(type: "double precision", nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRpcaAnomalyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRpcaAnomalyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLShapInteractionLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeatureA = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FeatureB = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InteractionScore = table.Column<double>(type: "double precision", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLShapInteractionLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLShapInteractionLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLCandleBertEncoder_MLModelId",
                table: "MLCandleBertEncoder",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCmimFeatureRankingLog_MLModelId_SelectionRank",
                table: "MLCmimFeatureRankingLog",
                columns: new[] { "MLModelId", "SelectionRank" });

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalAnomalyLog_MLModelId_DetectedAt",
                table: "MLConformalAnomalyLog",
                columns: new[] { "MLModelId", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLInputGradientNormLog_MLModelId",
                table: "MLInputGradientNormLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLKalmanCoefficientLog_MLModelId_FeatureName",
                table: "MLKalmanCoefficientLog",
                columns: new[] { "MLModelId", "FeatureName" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLotteryTicketLog_MLModelId",
                table: "MLLotteryTicketLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRegimeSynchronyLog_PrimarySymbol_ComputedAt",
                table: "MLRegimeSynchronyLog",
                columns: new[] { "PrimarySymbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRetrogradeFalsePatternLog_MLModelId_FailureMode",
                table: "MLRetrogradeFalsePatternLog",
                columns: new[] { "MLModelId", "FailureMode" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRpcaAnomalyLog_MLModelId",
                table: "MLRpcaAnomalyLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLShapInteractionLog_MLModelId_FeatureA_FeatureB",
                table: "MLShapInteractionLog",
                columns: new[] { "MLModelId", "FeatureA", "FeatureB" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLCandleBertEncoder");

            migrationBuilder.DropTable(
                name: "MLCmimFeatureRankingLog");

            migrationBuilder.DropTable(
                name: "MLConformalAnomalyLog");

            migrationBuilder.DropTable(
                name: "MLInputGradientNormLog");

            migrationBuilder.DropTable(
                name: "MLKalmanCoefficientLog");

            migrationBuilder.DropTable(
                name: "MLLotteryTicketLog");

            migrationBuilder.DropTable(
                name: "MLRegimeSynchronyLog");

            migrationBuilder.DropTable(
                name: "MLRetrogradeFalsePatternLog");

            migrationBuilder.DropTable(
                name: "MLRpcaAnomalyLog");

            migrationBuilder.DropTable(
                name: "MLShapInteractionLog");
        }
    }
}
