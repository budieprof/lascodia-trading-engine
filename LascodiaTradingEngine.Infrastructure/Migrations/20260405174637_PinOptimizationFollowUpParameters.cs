using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PinOptimizationFollowUpParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParametersSnapshotJson",
                table: "WalkForwardRun",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParametersSnapshotJson",
                table: "BacktestRun",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParametersSnapshotJson",
                table: "WalkForwardRun");

            migrationBuilder.DropColumn(
                name: "ParametersSnapshotJson",
                table: "BacktestRun");
        }
    }
}
