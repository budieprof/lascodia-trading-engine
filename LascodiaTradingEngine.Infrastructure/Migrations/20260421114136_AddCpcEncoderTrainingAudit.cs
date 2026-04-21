using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCpcEncoderTrainingAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLCpcEncoderTrainingLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    Regime = table.Column<int>(type: "integer", nullable: true),
                    EncoderType = table.Column<int>(type: "integer", nullable: false),
                    EvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PriorEncoderId = table.Column<long>(type: "bigint", nullable: true),
                    PriorInfoNceLoss = table.Column<double>(type: "double precision", precision: 12, scale: 6, nullable: true),
                    PromotedEncoderId = table.Column<long>(type: "bigint", nullable: true),
                    TrainInfoNceLoss = table.Column<double>(type: "double precision", precision: 12, scale: 6, nullable: true),
                    ValidationInfoNceLoss = table.Column<double>(type: "double precision", precision: 12, scale: 6, nullable: true),
                    CandlesLoaded = table.Column<int>(type: "integer", nullable: false),
                    CandlesAfterRegimeFilter = table.Column<int>(type: "integer", nullable: false),
                    TrainingSequences = table.Column<int>(type: "integer", nullable: false),
                    ValidationSequences = table.Column<int>(type: "integer", nullable: false),
                    TrainingDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    DiagnosticsJson = table.Column<string>(type: "text", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCpcEncoderTrainingLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLCpcEncoderTrainingLog_Outcome_Reason_EvaluatedAt",
                table: "MLCpcEncoderTrainingLog",
                columns: new[] { "Outcome", "Reason", "EvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCpcEncoderTrainingLog_Symbol_Timeframe_Regime_EvaluatedAt",
                table: "MLCpcEncoderTrainingLog",
                columns: new[] { "Symbol", "Timeframe", "Regime", "EvaluatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLCpcEncoderTrainingLog");
        }
    }
}
