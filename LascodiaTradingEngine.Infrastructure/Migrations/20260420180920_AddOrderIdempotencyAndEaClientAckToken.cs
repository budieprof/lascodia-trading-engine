using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderIdempotencyAndEaClientAckToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientAckToken",
                table: "EACommand",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Order_TradeSignalId_TradingAccountId_Unique",
                table: "Order",
                columns: new[] { "TradeSignalId", "TradingAccountId" },
                unique: true,
                filter: "\"TradeSignalId\" IS NOT NULL AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Order_TradeSignalId_TradingAccountId_Unique",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "ClientAckToken",
                table: "EACommand");
        }
    }
}
