using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleActiveTradingAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradingAccount_IsActive",
                table: "TradingAccount");

            migrationBuilder.CreateIndex(
                name: "IX_TradingAccount_IsActive_SingleTrue",
                table: "TradingAccount",
                column: "IsActive",
                unique: true,
                filter: "\"IsActive\" = true AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradingAccount_IsActive_SingleTrue",
                table: "TradingAccount");

            migrationBuilder.CreateIndex(
                name: "IX_TradingAccount_IsActive",
                table: "TradingAccount",
                column: "IsActive");
        }
    }
}
