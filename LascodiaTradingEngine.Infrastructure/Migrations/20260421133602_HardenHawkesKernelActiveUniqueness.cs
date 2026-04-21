using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenHawkesKernelActiveUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MLHawkesKernelParams_Symbol_Timeframe",
                table: "MLHawkesKernelParams",
                columns: new[] { "Symbol", "Timeframe" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLHawkesKernelParams_Symbol_Timeframe",
                table: "MLHawkesKernelParams");
        }
    }
}
