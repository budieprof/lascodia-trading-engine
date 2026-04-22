using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations.EventLogDb
{
    /// <inheritdoc />
    public partial class AddIntegrationEventLogStateCreationTimeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_IntegrationEventLog_State_CreationTime",
                table: "IntegrationEventLog",
                columns: new[] { "State", "CreationTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IntegrationEventLog_State_CreationTime",
                table: "IntegrationEventLog");
        }
    }
}
