using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCpcEncoderRegime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_IsActive",
                table: "MLCpcEncoder");

            migrationBuilder.AddColumn<int>(
                name: "Regime",
                table: "MLCpcEncoder",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_Regime_IsActive",
                table: "MLCpcEncoder",
                columns: new[] { "Symbol", "Timeframe", "Regime", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_Regime_IsActive",
                table: "MLCpcEncoder");

            migrationBuilder.DropColumn(
                name: "Regime",
                table: "MLCpcEncoder");

            migrationBuilder.CreateIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_IsActive",
                table: "MLCpcEncoder",
                columns: new[] { "Symbol", "Timeframe", "IsActive" });
        }
    }
}
