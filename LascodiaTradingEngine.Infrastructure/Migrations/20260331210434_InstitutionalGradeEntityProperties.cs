using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InstitutionalGradeEntityProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedCapacityLots",
                table: "Strategy",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleStage",
                table: "Strategy",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LifecycleStageEnteredAt",
                table: "Strategy",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AlgoDurationSeconds",
                table: "Order",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AlgoSliceCount",
                table: "Order",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionAlgorithm",
                table: "Order",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "GtdExpiresAt",
                table: "Order",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxSlippagePips",
                table: "Order",
                type: "numeric(18,5)",
                precision: 18,
                scale: 5,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeInForce",
                table: "Order",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "CandleIdRangeEnd",
                table: "MLTrainingRun",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CandleIdRangeStart",
                table: "MLTrainingRun",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DatasetHash",
                table: "MLTrainingRun",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DatasetHash",
                table: "MLModel",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FragilityScore",
                table: "MLModel",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PreviousChampionModelId",
                table: "MLModel",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoResolvedAt",
                table: "Alert",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CooldownSeconds",
                table: "Alert",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DeduplicationKey",
                table: "Alert",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "Alert",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AccountPerformanceAttribution",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    AttributionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartOfDayEquity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EndOfDayEquity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RealizedPnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UnrealizedPnlChange = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DailyReturnPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    StrategyAttributionJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    SymbolAttributionJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    MLAlphaPnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TimingAlphaPnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ExecutionCosts = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SharpeRatio7d = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SharpeRatio30d = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SortinoRatio30d = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    CalmarRatio30d = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    BenchmarkReturnPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AlphaVsBenchmarkPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ActiveReturnPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    InformationRatio = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    GrossAlphaPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ExecutionCostPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    NetAlphaPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TradeCount = table.Column<int>(type: "integer", nullable: false),
                    WinRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountPerformanceAttribution", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountPerformanceAttribution_TradingAccount_TradingAccount~",
                        column: x => x.TradingAccountId,
                        principalTable: "TradingAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalRequest",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetEntityId = table.Column<long>(type: "bigint", nullable: false),
                    TargetEntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ChangePayloadJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    RequestedByAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ApprovedByAccountId = table.Column<long>(type: "bigint", nullable: true),
                    ApproverComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EngineConfigAuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OldValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ChangedByAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineConfigAuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeatureVector",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CandleId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    BarTimestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Features = table.Column<byte[]>(type: "bytea", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    FeatureNamesJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureVector", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketDataAnomaly",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnomalyType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AnomalousValue = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    ExpectedValue = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    DeviationMagnitude = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    LastGoodBid = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    LastGoodAsk = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsReviewed = table.Column<bool>(type: "boolean", nullable: false),
                    WasQuarantined = table.Column<bool>(type: "boolean", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketDataAnomaly", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLModelLifecycleLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PreviousStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    NewStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PreviousChampionModelId = table.Column<long>(type: "bigint", nullable: true),
                    ShadowEvaluationId = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TriggeredByAccountId = table.Column<long>(type: "bigint", nullable: true),
                    DirectionAccuracyAtTransition = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    LiveAccuracyAtTransition = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    BrierScoreAtTransition = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelLifecycleLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelLifecycleLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderBookSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    BidPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    AskPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    BidVolume = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    AskVolume = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    SpreadPoints = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderBookSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedIdempotencyKey",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "integer", nullable: false),
                    ResponseBodyJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedIdempotencyKey", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SignalAllocation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradeSignalId = table.Column<long>(type: "bigint", nullable: false),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<long>(type: "bigint", nullable: true),
                    AllocatedLotSize = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    AllocationMethod = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AccountEquityAtAllocation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AllocationFraction = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    RiskCheckPassed = table.Column<bool>(type: "boolean", nullable: false),
                    RiskCheckBlockReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AllocatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalAllocation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignalAllocation_Order_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SignalAllocation_TradeSignal_TradeSignalId",
                        column: x => x.TradeSignalId,
                        principalTable: "TradeSignal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SignalAllocation_TradingAccount_TradingAccountId",
                        column: x => x.TradingAccountId,
                        principalTable: "TradingAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StrategyCapacity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AverageDailyVolume = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VolumeParticipationRatePct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CapacityCeilingLots = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    CurrentAggregateLots = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    UtilizationPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    MarketImpactCurveJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    EstimatedSlippageAtCurrentSize = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    CalibrationWindowDays = table.Column<int>(type: "integer", nullable: false),
                    EstimatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyCapacity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrategyCapacity_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StrategyVariant",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaseStrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParameterOverridesJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ShadowSignalCount = table.Column<int>(type: "integer", nullable: false),
                    RequiredSignals = table.Column<int>(type: "integer", nullable: false),
                    ShadowWinRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ShadowExpectedValue = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    ShadowSharpeRatio = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    BaseWinRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    BaseExpectedValue = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    IsPromoted = table.Column<bool>(type: "boolean", nullable: false),
                    ComparisonResultJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyVariant", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrategyVariant_Strategy_BaseStrategyId",
                        column: x => x.BaseStrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StressTestScenario",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ScenarioType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ShockDefinitionJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StressTestScenario", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TickRecord",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Bid = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Ask = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Mid = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SpreadPoints = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    TickVolume = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TickTimestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TickRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradeRationale",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    TradeSignalId = table.Column<long>(type: "bigint", nullable: false),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StrategyType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IndicatorValuesJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SignalConditionsMet = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RuleBasedDirection = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RuleBasedConfidence = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: true),
                    MLModelVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MLPredictedDirection = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    MLRawProbability = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    MLCalibratedProbability = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    MLServedProbability = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    MLDecisionThreshold = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    MLConfidenceScore = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    MLShapContributionsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    MLEnsembleDisagreement = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    MLKellyFraction = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    Tier1Passed = table.Column<bool>(type: "boolean", nullable: false),
                    Tier1BlockReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Tier2Passed = table.Column<bool>(type: "boolean", nullable: false),
                    Tier2BlockReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RiskCheckDetailsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    AccountEquityAtCheck = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ProjectedExposurePct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    RiskPerTradePct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    RequestedPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    FillPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    SlippagePips = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: true),
                    FillLatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    SpreadAtExecution = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    MarketRegimeAtSignal = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    RegimeConfidence = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    TradingSessionAtSignal = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeRationale", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradeRationale_Order_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TradeRationale_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TradeRationale_TradeSignal_TradeSignalId",
                        column: x => x.TradeSignalId,
                        principalTable: "TradeSignal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TransactionCostAnalysis",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    TradeSignalId = table.Column<long>(type: "bigint", nullable: true),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ArrivalPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    FillPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SubmissionPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    VwapBenchmark = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    ImplementationShortfall = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    DelayCost = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MarketImpactCost = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SpreadCost = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    CommissionCost = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    TotalCost = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    TotalCostBps = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    SignalToFillMs = table.Column<long>(type: "bigint", nullable: false),
                    SubmissionToFillMs = table.Column<long>(type: "bigint", nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionCostAnalysis", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransactionCostAnalysis_Order_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TransactionCostAnalysis_TradeSignal_TradeSignalId",
                        column: x => x.TradeSignalId,
                        principalTable: "TradeSignal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "WorkerHealthSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsRunning = table.Column<bool>(type: "boolean", nullable: false),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastCycleDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CycleDurationP50Ms = table.Column<long>(type: "bigint", nullable: false),
                    CycleDurationP95Ms = table.Column<long>(type: "bigint", nullable: false),
                    CycleDurationP99Ms = table.Column<long>(type: "bigint", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    ErrorsLastHour = table.Column<int>(type: "integer", nullable: false),
                    SuccessesLastHour = table.Column<int>(type: "integer", nullable: false),
                    BacklogDepth = table.Column<int>(type: "integer", nullable: false),
                    ConfiguredIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerHealthSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StressTestResult",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StressTestScenarioId = table.Column<long>(type: "bigint", nullable: false),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    PortfolioEquity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    StressedPnl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    StressedPnlPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    WouldTriggerMarginCall = table.Column<bool>(type: "boolean", nullable: false),
                    PositionImpactsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    MinimumShockPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    PortfolioVaR95 = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PortfolioCVaR95 = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StressTestResult", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StressTestResult_StressTestScenario_StressTestScenarioId",
                        column: x => x.StressTestScenarioId,
                        principalTable: "StressTestScenario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StressTestResult_TradingAccount_TradingAccountId",
                        column: x => x.TradingAccountId,
                        principalTable: "TradingAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLModel_PreviousChampionModelId",
                table: "MLModel",
                column: "PreviousChampionModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountPerformanceAttribution_TradingAccountId_AttributionD~",
                table: "AccountPerformanceAttribution",
                columns: new[] { "TradingAccountId", "AttributionDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequest_OperationType_TargetEntityId_Status",
                table: "ApprovalRequest",
                columns: new[] { "OperationType", "TargetEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EngineConfigAuditLog_Key_ChangedAt",
                table: "EngineConfigAuditLog",
                columns: new[] { "Key", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureVector_CandleId",
                table: "FeatureVector",
                column: "CandleId");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureVector_Symbol_Timeframe_BarTimestamp",
                table: "FeatureVector",
                columns: new[] { "Symbol", "Timeframe", "BarTimestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketDataAnomaly_Symbol_DetectedAt",
                table: "MarketDataAnomaly",
                columns: new[] { "Symbol", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelLifecycleLog_MLModelId_OccurredAt",
                table: "MLModelLifecycleLog",
                columns: new[] { "MLModelId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderBookSnapshot_Symbol_CapturedAt",
                table: "OrderBookSnapshot",
                columns: new[] { "Symbol", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedIdempotencyKey_ExpiresAt",
                table: "ProcessedIdempotencyKey",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedIdempotencyKey_Key",
                table: "ProcessedIdempotencyKey",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignalAllocation_OrderId",
                table: "SignalAllocation",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SignalAllocation_TradeSignalId",
                table: "SignalAllocation",
                column: "TradeSignalId");

            migrationBuilder.CreateIndex(
                name: "IX_SignalAllocation_TradingAccountId",
                table: "SignalAllocation",
                column: "TradingAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyCapacity_StrategyId_EstimatedAt",
                table: "StrategyCapacity",
                columns: new[] { "StrategyId", "EstimatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyVariant_BaseStrategyId_IsActive",
                table: "StrategyVariant",
                columns: new[] { "BaseStrategyId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_StressTestResult_StressTestScenarioId",
                table: "StressTestResult",
                column: "StressTestScenarioId");

            migrationBuilder.CreateIndex(
                name: "IX_StressTestResult_TradingAccountId_ExecutedAt",
                table: "StressTestResult",
                columns: new[] { "TradingAccountId", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TickRecord_Symbol_TickTimestamp",
                table: "TickRecord",
                columns: new[] { "Symbol", "TickTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_TradeRationale_OrderId",
                table: "TradeRationale",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradeRationale_StrategyId",
                table: "TradeRationale",
                column: "StrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeRationale_TradeSignalId",
                table: "TradeRationale",
                column: "TradeSignalId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionCostAnalysis_OrderId",
                table: "TransactionCostAnalysis",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransactionCostAnalysis_Symbol_AnalyzedAt",
                table: "TransactionCostAnalysis",
                columns: new[] { "Symbol", "AnalyzedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionCostAnalysis_TradeSignalId",
                table: "TransactionCostAnalysis",
                column: "TradeSignalId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHealthSnapshot_WorkerName_CapturedAt",
                table: "WorkerHealthSnapshot",
                columns: new[] { "WorkerName", "CapturedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_MLModel_MLModel_PreviousChampionModelId",
                table: "MLModel",
                column: "PreviousChampionModelId",
                principalTable: "MLModel",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MLModel_MLModel_PreviousChampionModelId",
                table: "MLModel");

            migrationBuilder.DropTable(
                name: "AccountPerformanceAttribution");

            migrationBuilder.DropTable(
                name: "ApprovalRequest");

            migrationBuilder.DropTable(
                name: "EngineConfigAuditLog");

            migrationBuilder.DropTable(
                name: "FeatureVector");

            migrationBuilder.DropTable(
                name: "MarketDataAnomaly");

            migrationBuilder.DropTable(
                name: "MLModelLifecycleLog");

            migrationBuilder.DropTable(
                name: "OrderBookSnapshot");

            migrationBuilder.DropTable(
                name: "ProcessedIdempotencyKey");

            migrationBuilder.DropTable(
                name: "SignalAllocation");

            migrationBuilder.DropTable(
                name: "StrategyCapacity");

            migrationBuilder.DropTable(
                name: "StrategyVariant");

            migrationBuilder.DropTable(
                name: "StressTestResult");

            migrationBuilder.DropTable(
                name: "TickRecord");

            migrationBuilder.DropTable(
                name: "TradeRationale");

            migrationBuilder.DropTable(
                name: "TransactionCostAnalysis");

            migrationBuilder.DropTable(
                name: "WorkerHealthSnapshot");

            migrationBuilder.DropTable(
                name: "StressTestScenario");

            migrationBuilder.DropIndex(
                name: "IX_MLModel_PreviousChampionModelId",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "EstimatedCapacityLots",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "LifecycleStage",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "LifecycleStageEnteredAt",
                table: "Strategy");

            migrationBuilder.DropColumn(
                name: "AlgoDurationSeconds",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "AlgoSliceCount",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "ExecutionAlgorithm",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "GtdExpiresAt",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "MaxSlippagePips",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "TimeInForce",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "CandleIdRangeEnd",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "CandleIdRangeStart",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "DatasetHash",
                table: "MLTrainingRun");

            migrationBuilder.DropColumn(
                name: "DatasetHash",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "FragilityScore",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "PreviousChampionModelId",
                table: "MLModel");

            migrationBuilder.DropColumn(
                name: "AutoResolvedAt",
                table: "Alert");

            migrationBuilder.DropColumn(
                name: "CooldownSeconds",
                table: "Alert");

            migrationBuilder.DropColumn(
                name: "DeduplicationKey",
                table: "Alert");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "Alert");
        }
    }
}
