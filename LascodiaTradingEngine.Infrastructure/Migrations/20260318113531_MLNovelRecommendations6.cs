using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLNovelRecommendations6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "LatestKyleLambda",
                table: "MLModel",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "MLAdwinDriftLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    Window1Mean = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Window2Mean = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EpsilonCut = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Window1Size = table.Column<int>(type: "integer", nullable: false),
                    Window2Size = table.Column<int>(type: "integer", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdwinDriftLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLAdwinDriftLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLBootstrapEnsemble",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    NumDraws = table.Column<int>(type: "integer", nullable: false),
                    DrawWeightsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    BootstrapAccuracy = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    TrainSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBootstrapEnsemble", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLBootstrapEnsemble_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLCorrelationSurpriseLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol1 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    BaselineRho = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CurrentRho = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    SurpriseZScore = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCorrelationSurpriseLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEvidentialParams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    GammaMean = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Nu = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Alpha = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Beta = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EpistemicUnc = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    AleatoricUnc = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    TrainSamples = table.Column<int>(type: "integer", nullable: false),
                    FittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEvidentialParams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEvidentialParams_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLHotellingDriftLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TSquaredStat = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CriticalValue = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHotellingDriftLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLHotellingDriftLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLRapsCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    Lambda = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    KReg = table.Column<int>(type: "integer", nullable: false),
                    QHat = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EmpiricalCoverage = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRapsCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRapsCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLRollSpreadLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    SerialCovariance = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EstimatedSpread = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRollSpreadLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSnapshotCheckpoint",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    CycleIndex = table.Column<int>(type: "integer", nullable: false),
                    LrAtCapture = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ValidationLoss = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    WeightsBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSnapshotCheckpoint", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSnapshotCheckpoint_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLVennAbersCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    IsotonicScores0Json = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    IsotonicScores1Json = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    MeanIntervalWidth = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLVennAbersCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLVennAbersCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLAdwinDriftLog_MLModelId_DetectedAt",
                table: "MLAdwinDriftLog",
                columns: new[] { "MLModelId", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLBootstrapEnsemble_MLModelId",
                table: "MLBootstrapEnsemble",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCorrelationSurpriseLog_Symbol1_Symbol2_Timeframe",
                table: "MLCorrelationSurpriseLog",
                columns: new[] { "Symbol1", "Symbol2", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEvidentialParams_MLModelId",
                table: "MLEvidentialParams",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHotellingDriftLog_MLModelId",
                table: "MLHotellingDriftLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRapsCalibration_MLModelId",
                table: "MLRapsCalibration",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRollSpreadLog_Symbol_Timeframe",
                table: "MLRollSpreadLog",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSnapshotCheckpoint_MLModelId_CycleIndex",
                table: "MLSnapshotCheckpoint",
                columns: new[] { "MLModelId", "CycleIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_MLVennAbersCalibration_MLModelId",
                table: "MLVennAbersCalibration",
                column: "MLModelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLAdwinDriftLog");

            migrationBuilder.DropTable(
                name: "MLBootstrapEnsemble");

            migrationBuilder.DropTable(
                name: "MLCorrelationSurpriseLog");

            migrationBuilder.DropTable(
                name: "MLEvidentialParams");

            migrationBuilder.DropTable(
                name: "MLHotellingDriftLog");

            migrationBuilder.DropTable(
                name: "MLRapsCalibration");

            migrationBuilder.DropTable(
                name: "MLRollSpreadLog");

            migrationBuilder.DropTable(
                name: "MLSnapshotCheckpoint");

            migrationBuilder.DropTable(
                name: "MLVennAbersCalibration");

            migrationBuilder.DropColumn(
                name: "LatestKyleLambda",
                table: "MLModel");
        }
    }
}
