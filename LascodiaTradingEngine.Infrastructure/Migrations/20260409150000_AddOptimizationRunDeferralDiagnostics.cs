using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    [DbContext(typeof(WriteApplicationDbContext))]
    [Migration("20260409150000_AddOptimizationRunDeferralDiagnostics")]
    public partial class AddOptimizationRunDeferralDiagnostics : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeferredAtUtc",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeferralCount",
                table: "OptimizationRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DeferralReason",
                table: "OptimizationRun",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeterministicSeedVersion",
                table: "OptimizationRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastResumedAtUtc",
                table: "OptimizationRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_LastResumedAtUtc",
                table: "OptimizationRun",
                column: "LastResumedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_DeferralCount_DeferredUntilUtc",
                table: "OptimizationRun",
                columns: new[] { "Status", "DeferralCount", "DeferredUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_DeferralReason_DeferredUntilUtc",
                table: "OptimizationRun",
                columns: new[] { "Status", "DeferralReason", "DeferredUntilUtc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_LastResumedAtUtc",
                table: "OptimizationRun");

            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_Status_DeferralCount_DeferredUntilUtc",
                table: "OptimizationRun");

            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_Status_DeferralReason_DeferredUntilUtc",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "DeferredAtUtc",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "DeferralCount",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "DeferralReason",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "DeterministicSeedVersion",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "LastResumedAtUtc",
                table: "OptimizationRun");
        }
    }
}
