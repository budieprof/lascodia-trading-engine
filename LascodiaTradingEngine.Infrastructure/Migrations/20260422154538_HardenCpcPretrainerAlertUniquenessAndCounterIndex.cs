using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenCpcPretrainerAlertUniquenessAndCounterIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MLCpcEncoderTrainingLog_Symbol_Timeframe_Regime_EncoderType~",
                table: "MLCpcEncoderTrainingLog",
                columns: new[] { "Symbol", "Timeframe", "Regime", "EncoderType", "EvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Alert_DeduplicationKey",
                table: "Alert",
                column: "DeduplicationKey",
                unique: true,
                filter: "\"IsActive\" = TRUE AND \"IsDeleted\" = FALSE AND \"DeduplicationKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLCpcEncoderTrainingLog_Symbol_Timeframe_Regime_EncoderType~",
                table: "MLCpcEncoderTrainingLog");

            migrationBuilder.DropIndex(
                name: "IX_Alert_DeduplicationKey",
                table: "Alert");
        }
    }
}
