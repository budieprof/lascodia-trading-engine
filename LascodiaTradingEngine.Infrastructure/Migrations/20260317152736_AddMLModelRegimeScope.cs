using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMLModelRegimeScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RegimeScope",
                table: "MLModel",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModel_Symbol_Timeframe_RegimeScope_IsActive",
                table: "MLModel",
                columns: new[] { "Symbol", "Timeframe", "RegimeScope", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLModel_Symbol_Timeframe_RegimeScope_IsActive",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "RegimeScope",
                table: "MLModel");
        }
    }
}
