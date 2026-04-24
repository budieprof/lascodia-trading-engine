using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleActiveTradingAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_TradingAccount_IsActive";

                WITH ranked_active_accounts AS (
                    SELECT
                        "Id",
                        ROW_NUMBER() OVER (
                            ORDER BY "LastSyncedAt" DESC, "Id" DESC
                        ) AS row_num
                    FROM "TradingAccount"
                    WHERE "IsActive" = true AND "IsDeleted" = false
                )
                UPDATE "TradingAccount" account
                SET "IsActive" = false
                FROM ranked_active_accounts ranked
                WHERE account."Id" = ranked."Id"
                  AND ranked.row_num > 1;

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_TradingAccount_IsActive_SingleTrue"
                ON "TradingAccount" ("IsActive")
                WHERE "IsActive" = true AND "IsDeleted" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_TradingAccount_IsActive_SingleTrue";

                CREATE INDEX IF NOT EXISTS "IX_TradingAccount_IsActive"
                ON "TradingAccount" ("IsActive");
                """);
        }
    }
}
