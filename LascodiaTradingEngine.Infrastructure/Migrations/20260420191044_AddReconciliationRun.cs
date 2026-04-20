using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReconciliationRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReconciliationRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OrphanedEnginePositions = table.Column<int>(type: "integer", nullable: false),
                    UnknownBrokerPositions = table.Column<int>(type: "integer", nullable: false),
                    MismatchedPositions = table.Column<int>(type: "integer", nullable: false),
                    OrphanedEngineOrders = table.Column<int>(type: "integer", nullable: false),
                    UnknownBrokerOrders = table.Column<int>(type: "integer", nullable: false),
                    TotalDrift = table.Column<int>(type: "integer", nullable: false),
                    BrokerPositionCount = table.Column<int>(type: "integer", nullable: false),
                    BrokerOrderCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRun", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRun_InstanceId_RunAt",
                table: "ReconciliationRun",
                columns: new[] { "InstanceId", "RunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRun_RunAt",
                table: "ReconciliationRun",
                column: "RunAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReconciliationRun");
        }
    }
}
