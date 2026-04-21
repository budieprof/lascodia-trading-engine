using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenCpcEncoderActiveUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_Regime_IsActive",
                table: "MLCpcEncoder");

            migrationBuilder.CreateIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_Regime",
                table: "MLCpcEncoder",
                columns: new[] { "Symbol", "Timeframe", "Regime" },
                unique: true,
                filter: "\"IsActive\" = true AND \"IsDeleted\" = false")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_Regime_EncoderType_IsActive",
                table: "MLCpcEncoder",
                columns: new[] { "Symbol", "Timeframe", "Regime", "EncoderType", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_Regime",
                table: "MLCpcEncoder");

            migrationBuilder.DropIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_Regime_EncoderType_IsActive",
                table: "MLCpcEncoder");

            migrationBuilder.CreateIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_Regime_IsActive",
                table: "MLCpcEncoder",
                columns: new[] { "Symbol", "Timeframe", "Regime", "IsActive" });
        }
    }
}
