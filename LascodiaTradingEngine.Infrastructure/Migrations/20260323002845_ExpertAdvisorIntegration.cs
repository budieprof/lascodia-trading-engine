using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExpertAdvisorIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EACommand",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TargetInstanceId = table.Column<string>(type: "text", nullable: false),
                    CommandType = table.Column<int>(type: "integer", nullable: false),
                    TargetTicket = table.Column<long>(type: "bigint", nullable: true),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Parameters = table.Column<string>(type: "text", nullable: true),
                    Acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AckResult = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EACommand", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EAInstance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<string>(type: "text", nullable: false),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Symbols = table.Column<string>(type: "text", nullable: false),
                    ChartSymbol = table.Column<string>(type: "text", nullable: false),
                    ChartTimeframe = table.Column<string>(type: "text", nullable: false),
                    IsCoordinator = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastHeartbeat = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EAVersion = table.Column<string>(type: "text", nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeregisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EAInstance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EAInstance_TradingAccount_TradingAccountId",
                        column: x => x.TradingAccountId,
                        principalTable: "TradingAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EAInstance_TradingAccountId",
                table: "EAInstance",
                column: "TradingAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EACommand");

            migrationBuilder.DropTable(
                name: "EAInstance");
        }
    }
}
