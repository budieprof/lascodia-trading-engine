using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrencyPairSwapFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SwapLong",
                table: "CurrencyPair",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "SwapMode",
                table: "CurrencyPair",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "SwapShort",
                table: "CurrencyPair",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SwapLong",
                table: "CurrencyPair");

            migrationBuilder.DropColumn(
                name: "SwapMode",
                table: "CurrencyPair");

            migrationBuilder.DropColumn(
                name: "SwapShort",
                table: "CurrencyPair");
        }
    }
}
