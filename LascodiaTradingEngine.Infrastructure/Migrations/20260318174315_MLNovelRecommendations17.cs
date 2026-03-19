using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations17 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLCrcLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RiskFunctionName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LambdaThreshold = table.Column<double>(type: "double precision", nullable: false),
                    EmpiricalRisk = table.Column<double>(type: "double precision", nullable: false),
                    RiskBound = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationSetSize = table.Column<int>(type: "integer", nullable: false),
                    CoverageAchieved = table.Column<double>(type: "double precision", nullable: false),
                    MeanSetSize = table.Column<double>(type: "double precision", nullable: false),
                    WorstCaseRisk = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCrcLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCrcLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLGaussianCopulaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TailDependenceLower = table.Column<double>(type: "double precision", nullable: false),
                    TailDependenceUpper = table.Column<double>(type: "double precision", nullable: false),
                    CopulaCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    MarginalKsStatistic = table.Column<double>(type: "double precision", nullable: false),
                    JointLogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    CopulaAicScore = table.Column<double>(type: "double precision", nullable: false),
                    PairCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGaussianCopulaLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLGaussianCopulaLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLOmdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PortfolioWeightsJson = table.Column<string>(type: "text", nullable: false),
                    GradientNorm = table.Column<double>(type: "double precision", nullable: false),
                    DualVariable = table.Column<double>(type: "double precision", nullable: false),
                    RegretBound = table.Column<double>(type: "double precision", nullable: false),
                    WeightEntropy = table.Column<double>(type: "double precision", nullable: false),
                    LearningRate = table.Column<double>(type: "double precision", nullable: false),
                    TotalSteps = table.Column<int>(type: "integer", nullable: false),
                    MirrorMapType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOmdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPersistentLaplacianLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FiedlerValue0 = table.Column<double>(type: "double precision", nullable: false),
                    FiedlerValue1 = table.Column<double>(type: "double precision", nullable: false),
                    SpectralGap0 = table.Column<double>(type: "double precision", nullable: false),
                    SpectralGap1 = table.Column<double>(type: "double precision", nullable: false),
                    MeanLaplacianEigenvalue = table.Column<double>(type: "double precision", nullable: false),
                    TopologicalComplexity = table.Column<double>(type: "double precision", nullable: false),
                    FilterThreshold = table.Column<double>(type: "double precision", nullable: false),
                    VertexCount = table.Column<int>(type: "integer", nullable: false),
                    EdgeCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPersistentLaplacianLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSpdNetLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BiMapLoss = table.Column<double>(type: "double precision", nullable: false),
                    FiedlerValue = table.Column<double>(type: "double precision", nullable: false),
                    SpectralGap = table.Column<double>(type: "double precision", nullable: false),
                    ReigActivationMean = table.Column<double>(type: "double precision", nullable: false),
                    RegimeSimilarity = table.Column<double>(type: "double precision", nullable: false),
                    CovarianceRank = table.Column<int>(type: "integer", nullable: false),
                    MatrixConditionNumber = table.Column<double>(type: "double precision", nullable: false),
                    PairCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSpdNetLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSvgdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ParticleCount = table.Column<int>(type: "integer", nullable: false),
                    MeanPrediction = table.Column<double>(type: "double precision", nullable: false),
                    PredictionVariance = table.Column<double>(type: "double precision", nullable: false),
                    KernelBandwidth = table.Column<double>(type: "double precision", nullable: false),
                    SteinGradientNorm = table.Column<double>(type: "double precision", nullable: false),
                    ParticleDiversity = table.Column<double>(type: "double precision", nullable: false),
                    ConvergedAtIteration = table.Column<int>(type: "integer", nullable: false),
                    EpistemicUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSvgdLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSvgdLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLSymbolicRegressionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BestExpressionJson = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BestR2Score = table.Column<double>(type: "double precision", nullable: false),
                    ExpressionComplexity = table.Column<int>(type: "integer", nullable: false),
                    Generations = table.Column<int>(type: "integer", nullable: false),
                    PopulationSize = table.Column<int>(type: "integer", nullable: false),
                    BestFitnessScore = table.Column<double>(type: "double precision", nullable: false),
                    DiscoveredOperators = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FeaturesUsedJson = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSymbolicRegressionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSymbolicRegressionLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLCrcLogs_ComputedAt",
                table: "MLCrcLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrcLogs_MLModelId",
                table: "MLCrcLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrcLogs_Symbol",
                table: "MLCrcLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLGaussianCopulaLogs_ComputedAt",
                table: "MLGaussianCopulaLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLGaussianCopulaLogs_MLModelId",
                table: "MLGaussianCopulaLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGaussianCopulaLogs_Symbol",
                table: "MLGaussianCopulaLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLOmdLogs_ComputedAt",
                table: "MLOmdLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLOmdLogs_Symbol",
                table: "MLOmdLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLPersistentLaplacianLogs_ComputedAt",
                table: "MLPersistentLaplacianLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLPersistentLaplacianLogs_Symbol",
                table: "MLPersistentLaplacianLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLSpdNetLogs_ComputedAt",
                table: "MLSpdNetLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLSpdNetLogs_Symbol",
                table: "MLSpdNetLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLSvgdLogs_ComputedAt",
                table: "MLSvgdLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLSvgdLogs_MLModelId",
                table: "MLSvgdLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSvgdLogs_Symbol",
                table: "MLSvgdLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLSymbolicRegressionLogs_ComputedAt",
                table: "MLSymbolicRegressionLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLSymbolicRegressionLogs_MLModelId",
                table: "MLSymbolicRegressionLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSymbolicRegressionLogs_Symbol",
                table: "MLSymbolicRegressionLogs",
                column: "Symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLCrcLogs");

            migrationBuilder.DropTable(
                name: "MLGaussianCopulaLogs");

            migrationBuilder.DropTable(
                name: "MLOmdLogs");

            migrationBuilder.DropTable(
                name: "MLPersistentLaplacianLogs");

            migrationBuilder.DropTable(
                name: "MLSpdNetLogs");

            migrationBuilder.DropTable(
                name: "MLSvgdLogs");

            migrationBuilder.DropTable(
                name: "MLSymbolicRegressionLogs");
        }
    }
}
