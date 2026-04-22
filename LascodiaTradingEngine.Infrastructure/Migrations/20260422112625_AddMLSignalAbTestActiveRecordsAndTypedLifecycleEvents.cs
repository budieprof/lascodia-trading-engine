using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMLSignalAbTestActiveRecordsAndTypedLifecycleEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MLSignalAbTest",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChampionModelId = table.Column<long>(type: "bigint", nullable: false),
                    ChallengerModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSignalAbTest", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelLifecycleLog_MLModelId_EventType",
                table: "MLModelLifecycleLog",
                columns: new[] { "MLModelId", "EventType" },
                unique: true,
                filter: "\"EventType\" = 'AbTestRejection' AND \"IsDeleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_MLModelLifecycleLog_MLModelId_EventType_PreviousChampionMod~",
                table: "MLModelLifecycleLog",
                columns: new[] { "MLModelId", "EventType", "PreviousChampionModelId" },
                unique: true,
                filter: "\"EventType\" = 'AbTestPromotion' AND \"IsDeleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_MLSignalAbTest_ChampionModelId_ChallengerModelId_Status",
                table: "MLSignalAbTest",
                columns: new[] { "ChampionModelId", "ChallengerModelId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSignalAbTest_StartedAtUtc",
                table: "MLSignalAbTest",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MLSignalAbTest_Symbol_Timeframe",
                table: "MLSignalAbTest",
                columns: new[] { "Symbol", "Timeframe" },
                unique: true,
                filter: "\"Status\" = 'Active' AND \"IsDeleted\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLSignalAbTest");

            migrationBuilder.DropIndex(
                name: "IX_MLModelLifecycleLog_MLModelId_EventType",
                table: "MLModelLifecycleLog");

            migrationBuilder.DropIndex(
                name: "IX_MLModelLifecycleLog_MLModelId_EventType_PreviousChampionMod~",
                table: "MLModelLifecycleLog");
        }
    }
}
