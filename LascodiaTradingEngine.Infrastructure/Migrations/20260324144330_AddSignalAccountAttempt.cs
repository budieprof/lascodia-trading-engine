using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalAccountAttempt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignalAccountAttempt",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradeSignalId = table.Column<long>(type: "bigint", nullable: false),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    BlockReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalAccountAttempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignalAccountAttempt_TradeSignal_TradeSignalId",
                        column: x => x.TradeSignalId,
                        principalTable: "TradeSignal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SignalAccountAttempt_TradingAccount_TradingAccountId",
                        column: x => x.TradingAccountId,
                        principalTable: "TradingAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignalAccountAttempt_TradeSignalId_TradingAccountId",
                table: "SignalAccountAttempt",
                columns: new[] { "TradeSignalId", "TradingAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignalAccountAttempt_TradingAccountId",
                table: "SignalAccountAttempt",
                column: "TradingAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignalAccountAttempt");
        }
    }
}
