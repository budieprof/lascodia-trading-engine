using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixEAInstanceShadowFkAndLifecycleFilter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EAInstance_TradingAccount_TradingAccountId1",
                table: "EAInstance");

            migrationBuilder.DropIndex(
                name: "IX_EAInstance_TradingAccountId1",
                table: "EAInstance");

            migrationBuilder.DropColumn(
                name: "TradingAccountId1",
                table: "EAInstance");

            migrationBuilder.AlterColumn<decimal>(
                name: "SwapAccumulated",
                table: "PositionLifecycleEvent",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Source",
                table: "PositionLifecycleEvent",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<decimal>(
                name: "PreviousLots",
                table: "PositionLifecycleEvent",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "NewLots",
                table: "PositionLifecycleEvent",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EventType",
                table: "PositionLifecycleEvent",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "PositionLifecycleEvent",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "CommissionAccumulated",
                table: "PositionLifecycleEvent",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PositionLifecycleEvent_OccurredAt",
                table: "PositionLifecycleEvent",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PositionLifecycleEvent_OccurredAt",
                table: "PositionLifecycleEvent");

            migrationBuilder.AlterColumn<decimal>(
                name: "SwapAccumulated",
                table: "PositionLifecycleEvent",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,8)",
                oldPrecision: 18,
                oldScale: 8,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Source",
                table: "PositionLifecycleEvent",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30);

            migrationBuilder.AlterColumn<decimal>(
                name: "PreviousLots",
                table: "PositionLifecycleEvent",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "NewLots",
                table: "PositionLifecycleEvent",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EventType",
                table: "PositionLifecycleEvent",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "PositionLifecycleEvent",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "CommissionAccumulated",
                table: "PositionLifecycleEvent",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,8)",
                oldPrecision: 18,
                oldScale: 8,
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TradingAccountId1",
                table: "EAInstance",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EAInstance_TradingAccountId1",
                table: "EAInstance",
                column: "TradingAccountId1");

            migrationBuilder.AddForeignKey(
                name: "FK_EAInstance_TradingAccount_TradingAccountId1",
                table: "EAInstance",
                column: "TradingAccountId1",
                principalTable: "TradingAccount",
                principalColumn: "Id");
        }
    }
}
