using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorRoleTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperatorRole",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedByAccountId = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorRole", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperatorRole_TradingAccountId",
                table: "OperatorRole",
                column: "TradingAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorRole_TradingAccountId_Role",
                table: "OperatorRole",
                columns: new[] { "TradingAccountId", "Role" },
                unique: true,
                filter: "\"IsDeleted\" = FALSE");

            // Grace-period seed (E9 design doc): every existing TradingAccount gets the
            // Operator role so the rollout doesn't lock current users out the moment the
            // policy attributes hit production. Audit and downgrade after deploy.
            migrationBuilder.Sql(
                "INSERT INTO \"OperatorRole\" (\"TradingAccountId\", \"Role\", \"AssignedAt\", \"IsDeleted\", \"OutboxId\") " +
                "SELECT \"Id\", 'Operator', NOW() AT TIME ZONE 'UTC', FALSE, gen_random_uuid() FROM \"TradingAccount\" " +
                "WHERE \"IsDeleted\" = FALSE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperatorRole");
        }
    }
}
