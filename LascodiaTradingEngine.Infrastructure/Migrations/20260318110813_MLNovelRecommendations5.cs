using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCvbEnsemble",
                table: "MLModel",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MLDmlEffectLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CausalEffect = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    StandardError = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    PValue = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsSignificant = table.Column<bool>(type: "boolean", nullable: false),
                    FoldCount = table.Column<int>(type: "integer", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDmlEffectLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLDmlEffectLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLEceLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Ece = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EceEwma = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    NumBins = table.Column<int>(type: "integer", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEceLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEceLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLEvtRiskEstimate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    GpdShape = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    GpdScale = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Var99 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CVaR99 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    TailThreshold = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    TailSamples = table.Column<int>(type: "integer", nullable: false),
                    TotalSamples = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEvtRiskEstimate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEvtRiskEstimate_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLIbEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    InputDim = table.Column<int>(type: "integer", nullable: false),
                    LatentDim = table.Column<int>(type: "integer", nullable: false),
                    Beta = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    MutualInfoZY = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    MutualInfoZX = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EncoderBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLIbEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLModelGoodnessOfFit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    McFaddenR2 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CoxSnellR2 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    NullLogLikelihood = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelGoodnessOfFit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelGoodnessOfFit_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLNmfLatentBasis",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    NumComponents = table.Column<int>(type: "integer", nullable: false),
                    InputDim = table.Column<int>(type: "integer", nullable: false),
                    BasisMatrixJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNmfLatentBasis", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLOptimalStoppingLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    MeanConfidenceThreshold = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    TrialsTotal = table.Column<int>(type: "integer", nullable: false),
                    TrialsStopped = table.Column<int>(type: "integer", nullable: false),
                    EmpiricalImprovementRate = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOptimalStoppingLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLOptimalStoppingLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLRenyiDivergenceLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RenyiDivergence = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Alpha = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRenyiDivergenceLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRenyiDivergenceLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLSomModel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    GridRows = table.Column<int>(type: "integer", nullable: false),
                    GridCols = table.Column<int>(type: "integer", nullable: false),
                    InputDim = table.Column<int>(type: "integer", nullable: false),
                    WeightsJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    QuantisationError = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSomModel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSparseEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    InputDim = table.Column<int>(type: "integer", nullable: false),
                    LatentDim = table.Column<int>(type: "integer", nullable: false),
                    SparsityK = table.Column<int>(type: "integer", nullable: false),
                    ReconstructionLoss = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EncoderBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSparseEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLWganCheckpoint",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    GeneratorDim = table.Column<int>(type: "integer", nullable: false),
                    DiscriminatorDim = table.Column<int>(type: "integer", nullable: false),
                    WassersteinDistance = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    GeneratorBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    DiscriminatorBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainingEpochs = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLWganCheckpoint", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLDmlEffectLog_MLModelId",
                table: "MLDmlEffectLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEceLog_MLModelId",
                table: "MLEceLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEvtRiskEstimate_MLModelId",
                table: "MLEvtRiskEstimate",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLIbEncoder_Symbol_Timeframe",
                table: "MLIbEncoder",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelGoodnessOfFit_MLModelId",
                table: "MLModelGoodnessOfFit",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLNmfLatentBasis_Symbol_Timeframe",
                table: "MLNmfLatentBasis",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLOptimalStoppingLog_MLModelId",
                table: "MLOptimalStoppingLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRenyiDivergenceLog_MLModelId",
                table: "MLRenyiDivergenceLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSomModel_Symbol_Timeframe",
                table: "MLSomModel",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSparseEncoder_Symbol_Timeframe",
                table: "MLSparseEncoder",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLWganCheckpoint_Symbol_Timeframe",
                table: "MLWganCheckpoint",
                columns: new[] { "Symbol", "Timeframe" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLDmlEffectLog");

            migrationBuilder.DropTable(
                name: "MLEceLog");

            migrationBuilder.DropTable(
                name: "MLEvtRiskEstimate");

            migrationBuilder.DropTable(
                name: "MLIbEncoder");

            migrationBuilder.DropTable(
                name: "MLModelGoodnessOfFit");

            migrationBuilder.DropTable(
                name: "MLNmfLatentBasis");

            migrationBuilder.DropTable(
                name: "MLOptimalStoppingLog");

            migrationBuilder.DropTable(
                name: "MLRenyiDivergenceLog");

            migrationBuilder.DropTable(
                name: "MLSomModel");

            migrationBuilder.DropTable(
                name: "MLSparseEncoder");

            migrationBuilder.DropTable(
                name: "MLWganCheckpoint");

            migrationBuilder.DropColumn(
                name: "IsCvbEnsemble",
                table: "MLModel");
        }
    }
}
