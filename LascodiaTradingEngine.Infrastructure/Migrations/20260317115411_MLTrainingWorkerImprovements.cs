using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MLTrainingWorkerImprovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BacktestRun_Strategy_StrategyId",
                table: "BacktestRun");

            migrationBuilder.DropForeignKey(
                name: "FK_ExecutionQualityLog_Strategy_StrategyId",
                table: "ExecutionQualityLog");

            migrationBuilder.DropForeignKey(
                name: "FK_MLModelPredictionLog_MLModel_MLModelId",
                table: "MLModelPredictionLog");

            migrationBuilder.DropForeignKey(
                name: "FK_MLModelPredictionLog_TradeSignal_TradeSignalId",
                table: "MLModelPredictionLog");

            migrationBuilder.DropForeignKey(
                name: "FK_MLTrainingRun_MLModel_MLModelId",
                table: "MLTrainingRun");

            migrationBuilder.DropForeignKey(
                name: "FK_OptimizationRun_Strategy_StrategyId",
                table: "OptimizationRun");

            migrationBuilder.DropForeignKey(
                name: "FK_Order_Strategy_StrategyId",
                table: "Order");

            migrationBuilder.DropForeignKey(
                name: "FK_Order_TradeSignal_TradeSignalId",
                table: "Order");

            migrationBuilder.DropForeignKey(
                name: "FK_Order_TradingAccount_TradingAccountId",
                table: "Order");

            migrationBuilder.DropForeignKey(
                name: "FK_PositionScaleOrder_Order_OrderId",
                table: "PositionScaleOrder");

            migrationBuilder.DropForeignKey(
                name: "FK_Strategy_RiskProfile_RiskProfileId",
                table: "Strategy");

            migrationBuilder.DropForeignKey(
                name: "FK_StrategyAllocation_Strategy_StrategyId",
                table: "StrategyAllocation");

            migrationBuilder.DropForeignKey(
                name: "FK_StrategyPerformanceSnapshot_Strategy_StrategyId",
                table: "StrategyPerformanceSnapshot");

            migrationBuilder.DropForeignKey(
                name: "FK_TradeSignal_MLModel_MLModelId",
                table: "TradeSignal");

            migrationBuilder.DropForeignKey(
                name: "FK_TradeSignal_Strategy_StrategyId",
                table: "TradeSignal");

            migrationBuilder.DropForeignKey(
                name: "FK_TradingAccount_Broker_BrokerId",
                table: "TradingAccount");

            migrationBuilder.DropForeignKey(
                name: "FK_WalkForwardRun_Strategy_StrategyId",
                table: "WalkForwardRun");

            migrationBuilder.AlterColumn<decimal>(
                name: "TrailingStopValue",
                table: "Position",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "Position",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "BrokerPositionId",
                table: "Position",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BrierScore",
                table: "MLTrainingRun",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedValue",
                table: "MLTrainingRun",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "F1Score",
                table: "MLTrainingRun",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PickedUpAt",
                table: "MLTrainingRun",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TrainingDurationMs",
                table: "MLTrainingRun",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "MLShadowEvaluation",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PromotionThreshold",
                table: "MLShadowEvaluation",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AlterColumn<string>(
                name: "ModelVersion",
                table: "MLModel",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<decimal>(
                name: "BrierScore",
                table: "MLModel",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedValue",
                table: "MLModel",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "F1Score",
                table: "MLModel",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ModelBytes",
                table: "MLModel",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WalkForwardAvgAccuracy",
                table: "MLModel",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WalkForwardFolds",
                table: "MLModel",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "WalkForwardStdDev",
                table: "MLModel",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "EngineConfig",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DataType",
                table: "EngineConfig",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "RecoveryMode",
                table: "DrawdownSnapshot",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<decimal>(
                name: "InitialBalance",
                table: "BacktestRun",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_StrategyId_Status",
                table: "WalkForwardRun",
                columns: new[] { "StrategyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_StrategyId_Status",
                table: "OptimizationRun",
                columns: new[] { "StrategyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_StrategyId_Status",
                table: "BacktestRun",
                columns: new[] { "StrategyId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_BacktestRun_Strategy_StrategyId",
                table: "BacktestRun",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ExecutionQualityLog_Strategy_StrategyId",
                table: "ExecutionQualityLog",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MLModelPredictionLog_MLModel_MLModelId",
                table: "MLModelPredictionLog",
                column: "MLModelId",
                principalTable: "MLModel",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MLModelPredictionLog_TradeSignal_TradeSignalId",
                table: "MLModelPredictionLog",
                column: "TradeSignalId",
                principalTable: "TradeSignal",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MLTrainingRun_MLModel_MLModelId",
                table: "MLTrainingRun",
                column: "MLModelId",
                principalTable: "MLModel",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_OptimizationRun_Strategy_StrategyId",
                table: "OptimizationRun",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Order_Strategy_StrategyId",
                table: "Order",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Order_TradeSignal_TradeSignalId",
                table: "Order",
                column: "TradeSignalId",
                principalTable: "TradeSignal",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Order_TradingAccount_TradingAccountId",
                table: "Order",
                column: "TradingAccountId",
                principalTable: "TradingAccount",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PositionScaleOrder_Order_OrderId",
                table: "PositionScaleOrder",
                column: "OrderId",
                principalTable: "Order",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Strategy_RiskProfile_RiskProfileId",
                table: "Strategy",
                column: "RiskProfileId",
                principalTable: "RiskProfile",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_StrategyAllocation_Strategy_StrategyId",
                table: "StrategyAllocation",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StrategyPerformanceSnapshot_Strategy_StrategyId",
                table: "StrategyPerformanceSnapshot",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TradeSignal_MLModel_MLModelId",
                table: "TradeSignal",
                column: "MLModelId",
                principalTable: "MLModel",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TradeSignal_Strategy_StrategyId",
                table: "TradeSignal",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TradingAccount_Broker_BrokerId",
                table: "TradingAccount",
                column: "BrokerId",
                principalTable: "Broker",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WalkForwardRun_Strategy_StrategyId",
                table: "WalkForwardRun",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BacktestRun_Strategy_StrategyId",
                table: "BacktestRun");

            migrationBuilder.DropForeignKey(
                name: "FK_ExecutionQualityLog_Strategy_StrategyId",
                table: "ExecutionQualityLog");

            migrationBuilder.DropForeignKey(
                name: "FK_MLModelPredictionLog_MLModel_MLModelId",
                table: "MLModelPredictionLog");

            migrationBuilder.DropForeignKey(
                name: "FK_MLModelPredictionLog_TradeSignal_TradeSignalId",
                table: "MLModelPredictionLog");

            migrationBuilder.DropForeignKey(
                name: "FK_MLTrainingRun_MLModel_MLModelId",
                table: "MLTrainingRun");

            migrationBuilder.DropForeignKey(
                name: "FK_OptimizationRun_Strategy_StrategyId",
                table: "OptimizationRun");

            migrationBuilder.DropForeignKey(
                name: "FK_Order_Strategy_StrategyId",
                table: "Order");

            migrationBuilder.DropForeignKey(
                name: "FK_Order_TradeSignal_TradeSignalId",
                table: "Order");

            migrationBuilder.DropForeignKey(
                name: "FK_Order_TradingAccount_TradingAccountId",
                table: "Order");

            migrationBuilder.DropForeignKey(
                name: "FK_PositionScaleOrder_Order_OrderId",
                table: "PositionScaleOrder");

            migrationBuilder.DropForeignKey(
                name: "FK_Strategy_RiskProfile_RiskProfileId",
                table: "Strategy");

            migrationBuilder.DropForeignKey(
                name: "FK_StrategyAllocation_Strategy_StrategyId",
                table: "StrategyAllocation");

            migrationBuilder.DropForeignKey(
                name: "FK_StrategyPerformanceSnapshot_Strategy_StrategyId",
                table: "StrategyPerformanceSnapshot");

            migrationBuilder.DropForeignKey(
                name: "FK_TradeSignal_MLModel_MLModelId",
                table: "TradeSignal");

            migrationBuilder.DropForeignKey(
                name: "FK_TradeSignal_Strategy_StrategyId",
                table: "TradeSignal");

            migrationBuilder.DropForeignKey(
                name: "FK_TradingAccount_Broker_BrokerId",
                table: "TradingAccount");

            migrationBuilder.DropForeignKey(
                name: "FK_WalkForwardRun_Strategy_StrategyId",
                table: "WalkForwardRun");

            migrationBuilder.DropIndex(
                name: "IX_WalkForwardRun_StrategyId_Status",
                table: "WalkForwardRun");

            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_StrategyId_Status",
                table: "OptimizationRun");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRun_StrategyId_Status",
                table: "BacktestRun");

            migrationBuilder.DropColumn(
                name: "BrierScore",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "ExpectedValue",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "F1Score",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "PickedUpAt",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "TrainingDurationMs",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "MLShadowEvaluation");

            migrationBuilder.DropColumn(
                name: "PromotionThreshold",
                table: "MLShadowEvaluation");

            migrationBuilder.DropColumn(
                name: "BrierScore",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "ExpectedValue",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "F1Score",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "ModelBytes",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "WalkForwardAvgAccuracy",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "WalkForwardFolds",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "WalkForwardStdDev",
                table: "MLModel");

            migrationBuilder.AlterColumn<decimal>(
                name: "TrailingStopValue",
                table: "Position",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,8)",
                oldPrecision: 18,
                oldScale: 8,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "Position",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<string>(
                name: "BrokerPositionId",
                table: "Position",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModelVersion",
                table: "MLModel",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "EngineConfig",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "DataType",
                table: "EngineConfig",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<int>(
                name: "RecoveryMode",
                table: "DrawdownSnapshot",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<decimal>(
                name: "InitialBalance",
                table: "BacktestRun",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AddForeignKey(
                name: "FK_BacktestRun_Strategy_StrategyId",
                table: "BacktestRun",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ExecutionQualityLog_Strategy_StrategyId",
                table: "ExecutionQualityLog",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MLModelPredictionLog_MLModel_MLModelId",
                table: "MLModelPredictionLog",
                column: "MLModelId",
                principalTable: "MLModel",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MLModelPredictionLog_TradeSignal_TradeSignalId",
                table: "MLModelPredictionLog",
                column: "TradeSignalId",
                principalTable: "TradeSignal",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MLTrainingRun_MLModel_MLModelId",
                table: "MLTrainingRun",
                column: "MLModelId",
                principalTable: "MLModel",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_OptimizationRun_Strategy_StrategyId",
                table: "OptimizationRun",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Order_Strategy_StrategyId",
                table: "Order",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Order_TradeSignal_TradeSignalId",
                table: "Order",
                column: "TradeSignalId",
                principalTable: "TradeSignal",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Order_TradingAccount_TradingAccountId",
                table: "Order",
                column: "TradingAccountId",
                principalTable: "TradingAccount",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PositionScaleOrder_Order_OrderId",
                table: "PositionScaleOrder",
                column: "OrderId",
                principalTable: "Order",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Strategy_RiskProfile_RiskProfileId",
                table: "Strategy",
                column: "RiskProfileId",
                principalTable: "RiskProfile",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_StrategyAllocation_Strategy_StrategyId",
                table: "StrategyAllocation",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StrategyPerformanceSnapshot_Strategy_StrategyId",
                table: "StrategyPerformanceSnapshot",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TradeSignal_MLModel_MLModelId",
                table: "TradeSignal",
                column: "MLModelId",
                principalTable: "MLModel",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_TradeSignal_Strategy_StrategyId",
                table: "TradeSignal",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TradingAccount_Broker_BrokerId",
                table: "TradingAccount",
                column: "BrokerId",
                principalTable: "Broker",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_WalkForwardRun_Strategy_StrategyId",
                table: "WalkForwardRun",
                column: "StrategyId",
                principalTable: "Strategy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
