using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    [DbContext(typeof(WriteApplicationDbContext))]
    [Migration("20260329173000_AddServedMlPredictionProbability")]
    public partial class AddServedMlPredictionProbability : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ServedCalibratedProbability",
                table: "MLModelPredictionLog",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServedCalibratedProbability",
                table: "MLModelPredictionLog");
        }
    }
}
