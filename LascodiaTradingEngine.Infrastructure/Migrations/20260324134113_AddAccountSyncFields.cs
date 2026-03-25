using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountSyncFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TradingAccount_Broker_BrokerId",
                table: "TradingAccount");

            migrationBuilder.DropTable(
                name: "Broker");

            migrationBuilder.DropIndex(
                name: "IX_TradingAccount_BrokerId",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "BrokerId",
                table: "TradingAccount");

            migrationBuilder.AddColumn<string>(
                name: "AccountType",
                table: "TradingAccount",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BrokerName",
                table: "TradingAccount",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BrokerServer",
                table: "TradingAccount",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "Credit",
                table: "TradingAccount",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedPassword",
                table: "TradingAccount",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "Leverage",
                table: "TradingAccount",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MarginLevel",
                table: "TradingAccount",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "MarginMode",
                table: "TradingAccount",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "MarginSoCall",
                table: "TradingAccount",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "MarginSoMode",
                table: "TradingAccount",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "MarginSoStopOut",
                table: "TradingAccount",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Profit",
                table: "TradingAccount",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxAbsoluteRiskPerTrade",
                table: "RiskProfile",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "MaxConsecutiveLosses",
                table: "RiskProfile",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxCorrelatedPositions",
                table: "RiskProfile",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxPositionsPerSymbol",
                table: "RiskProfile",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxTotalExposurePct",
                table: "RiskProfile",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MinEquityFloor",
                table: "RiskProfile",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MinRiskRewardRatio",
                table: "RiskProfile",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MinStopLossDistancePips",
                table: "RiskProfile",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "RequireStopLoss",
                table: "RiskProfile",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "WeekendGapRiskMultiplier",
                table: "RiskProfile",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PipSize",
                table: "CurrencyPair",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_TradingAccount_AccountId_BrokerServer",
                table: "TradingAccount",
                columns: new[] { "AccountId", "BrokerServer" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradingAccount_AccountId_BrokerServer",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "AccountType",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "BrokerName",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "BrokerServer",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "Credit",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "EncryptedPassword",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "Leverage",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "MarginLevel",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "MarginMode",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "MarginSoCall",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "MarginSoMode",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "MarginSoStopOut",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "Profit",
                table: "TradingAccount");

            migrationBuilder.DropColumn(
                name: "MaxAbsoluteRiskPerTrade",
                table: "RiskProfile");

            migrationBuilder.DropColumn(
                name: "MaxConsecutiveLosses",
                table: "RiskProfile");

            migrationBuilder.DropColumn(
                name: "MaxCorrelatedPositions",
                table: "RiskProfile");

            migrationBuilder.DropColumn(
                name: "MaxPositionsPerSymbol",
                table: "RiskProfile");

            migrationBuilder.DropColumn(
                name: "MaxTotalExposurePct",
                table: "RiskProfile");

            migrationBuilder.DropColumn(
                name: "MinEquityFloor",
                table: "RiskProfile");

            migrationBuilder.DropColumn(
                name: "MinRiskRewardRatio",
                table: "RiskProfile");

            migrationBuilder.DropColumn(
                name: "MinStopLossDistancePips",
                table: "RiskProfile");

            migrationBuilder.DropColumn(
                name: "RequireStopLoss",
                table: "RiskProfile");

            migrationBuilder.DropColumn(
                name: "WeekendGapRiskMultiplier",
                table: "RiskProfile");

            migrationBuilder.DropColumn(
                name: "PipSize",
                table: "CurrencyPair");

            migrationBuilder.AddColumn<long>(
                name: "BrokerId",
                table: "TradingAccount",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "Broker",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApiKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApiSecret = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BrokerType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsPaper = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StatusMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Broker", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradingAccount_BrokerId",
                table: "TradingAccount",
                column: "BrokerId");

            migrationBuilder.CreateIndex(
                name: "IX_Broker_BrokerType",
                table: "Broker",
                column: "BrokerType");

            migrationBuilder.CreateIndex(
                name: "IX_Broker_IsActive",
                table: "Broker",
                column: "IsActive");

            migrationBuilder.AddForeignKey(
                name: "FK_TradingAccount_Broker_BrokerId",
                table: "TradingAccount",
                column: "BrokerId",
                principalTable: "Broker",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
