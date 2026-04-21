using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCorrelatedFailureEvaluationDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveModelCount",
                table: "MLCorrelatedFailureLog",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EvaluatedModelCount",
                table: "MLCorrelatedFailureLog",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FailureDetailsJson",
                table: "MLCorrelatedFailureLog",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveModelCount",
                table: "MLCorrelatedFailureLog");

            migrationBuilder.DropColumn(
                name: "EvaluatedModelCount",
                table: "MLCorrelatedFailureLog");

            migrationBuilder.DropColumn(
                name: "FailureDetailsJson",
                table: "MLCorrelatedFailureLog");
        }
    }
}
