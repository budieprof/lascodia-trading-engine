using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    [DbContext(typeof(WriteApplicationDbContext))]
    [Migration("20260409113000_AddActiveStrategyGenerationKey")]
    public partial class AddActiveStrategyGenerationKey : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Strategy_ActiveGenerationKey",
                table: "Strategy",
                columns: new[] { "StrategyType", "Symbol", "Timeframe" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Strategy_ActiveGenerationKey",
                table: "Strategy");
        }
    }
}
