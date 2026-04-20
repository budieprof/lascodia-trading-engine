using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalRejectionAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignalRejectionAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradeSignalId = table.Column<long>(type: "bigint", nullable: true),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RejectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalRejectionAudit", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignalRejectionAudit_RejectedAt",
                table: "SignalRejectionAudit",
                column: "RejectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SignalRejectionAudit_Stage_Reason_RejectedAt",
                table: "SignalRejectionAudit",
                columns: new[] { "Stage", "Reason", "RejectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SignalRejectionAudit_StrategyId_RejectedAt",
                table: "SignalRejectionAudit",
                columns: new[] { "StrategyId", "RejectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SignalRejectionAudit_Symbol_RejectedAt",
                table: "SignalRejectionAudit",
                columns: new[] { "Symbol", "RejectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SignalRejectionAudit_TradeSignalId",
                table: "SignalRejectionAudit",
                column: "TradeSignalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignalRejectionAudit");
        }
    }
}
