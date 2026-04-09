using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    [DbContext(typeof(WriteApplicationDbContext))]
    [Migration("20260409140000_AddBacktestValidationQueueKeyIndex")]
    public partial class AddBacktestValidationQueueKeyIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_ActiveValidationQueueKey",
                table: "BacktestRun",
                column: "ValidationQueueKey",
                unique: true,
                filter: "\"ValidationQueueKey\" IS NOT NULL AND \"Status\" IN ('Queued','Running') AND \"IsDeleted\" = false");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_ActiveValidationQueueKey",
                table: "BacktestRun");
        }
    }
}
