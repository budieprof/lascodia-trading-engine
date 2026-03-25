using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadLetterAndPartialFillResubmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ParentOrderId",
                table: "Order",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Impact",
                table: "EconomicEvent",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.CreateTable(
                name: "DeadLetterEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HandlerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventPayload = table.Column<string>(type: "text", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    StackTrace = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    DeadLetteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsResolved = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadLetterEvent", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterEvent_DeadLetteredAt",
                table: "DeadLetterEvent",
                column: "DeadLetteredAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterEvent_IsResolved",
                table: "DeadLetterEvent",
                column: "IsResolved",
                filter: "\"IsResolved\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeadLetterEvent");

            migrationBuilder.DropColumn(
                name: "ParentOrderId",
                table: "Order");

            migrationBuilder.AlterColumn<string>(
                name: "Impact",
                table: "EconomicEvent",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);
        }
    }
}
