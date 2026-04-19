using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperExecutionIsSynthetic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSynthetic",
                table: "PaperExecution",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_PaperExecution_StrategyId_IsSynthetic",
                table: "PaperExecution",
                columns: new[] { "StrategyId", "IsSynthetic" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaperExecution_StrategyId_IsSynthetic",
                table: "PaperExecution");

            migrationBuilder.DropColumn(
                name: "IsSynthetic",
                table: "PaperExecution");
        }
    }
}
