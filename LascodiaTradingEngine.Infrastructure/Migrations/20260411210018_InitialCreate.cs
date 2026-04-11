using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alert",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Destination = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ConditionJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastTriggeredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Severity = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DeduplicationKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CooldownSeconds = table.Column<int>(type: "integer", nullable: false),
                    AutoResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alert", x => x.Id);
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
                name: "BrokerAccountSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<string>(type: "text", nullable: false),
                    Balance = table.Column<decimal>(type: "numeric", nullable: false),
                    Equity = table.Column<decimal>(type: "numeric", nullable: false),
                    MarginUsed = table.Column<decimal>(type: "numeric", nullable: false),
                    FreeMargin = table.Column<decimal>(type: "numeric", nullable: false),
                    ReportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrokerAccountSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Candle",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    Open = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    High = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Low = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Close = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Volume = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Candle", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "COTReport",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ReportDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CommercialLong = table.Column<long>(type: "bigint", nullable: false),
                    CommercialShort = table.Column<long>(type: "bigint", nullable: false),
                    NonCommercialLong = table.Column<long>(type: "bigint", nullable: false),
                    NonCommercialShort = table.Column<long>(type: "bigint", nullable: false),
                    RetailLong = table.Column<long>(type: "bigint", nullable: false),
                    RetailShort = table.Column<long>(type: "bigint", nullable: false),
                    NetNonCommercialPositioning = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalOpenInterest = table.Column<long>(type: "bigint", nullable: false),
                    NetPositioningChangeWeekly = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_COTReport", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CurrencyPair",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    BaseCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    QuoteCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DecimalPlaces = table.Column<int>(type: "integer", nullable: false),
                    ContractSize = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    PipSize = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MinLotSize = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    MaxLotSize = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    LotStep = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TradingHoursJson = table.Column<string>(type: "text", nullable: true),
                    SpreadPoints = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencyPair", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "DecisionLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    DecisionType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    ContextJson = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DrawdownSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CurrentEquity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    PeakEquity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DrawdownPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    RecoveryMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrawdownSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EACommand",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TargetInstanceId = table.Column<string>(type: "text", nullable: false),
                    CommandType = table.Column<int>(type: "integer", nullable: false),
                    TargetTicket = table.Column<long>(type: "bigint", nullable: true),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Parameters = table.Column<string>(type: "text", nullable: true),
                    Acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AckResult = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EACommand", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EconomicEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Impact = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Forecast = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Previous = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Actual = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ExternalKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EconomicEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EngineConfig",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DataType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsHotReloadable = table.Column<bool>(type: "boolean", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineConfig", x => x.Id);
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
                    SchemaHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureVector", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeatureVectorLineage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    SchemaHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OldestCandleUsed = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NewestCandleUsed = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CandleCount = table.Column<int>(type: "integer", nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureVectorLineage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LivePrice",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Bid = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Ask = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LivePrice", x => x.Id);
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
                name: "MarketRegimeSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Regime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    ADX = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    ATR = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    BollingerBandWidth = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketRegimeSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCorrelatedFailureLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FailingModelCount = table.Column<int>(type: "integer", nullable: false),
                    TotalModelCount = table.Column<int>(type: "integer", nullable: false),
                    FailureRatio = table.Column<double>(type: "double precision", nullable: false),
                    SymbolsAffectedJson = table.Column<string>(type: "text", nullable: false),
                    PauseActivated = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCorrelatedFailureLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCpcEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    EmbeddingDim = table.Column<int>(type: "integer", nullable: false),
                    PredictionSteps = table.Column<int>(type: "integer", nullable: false),
                    InfoNceLoss = table.Column<double>(type: "double precision", precision: 12, scale: 6, nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    EncoderBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCpcEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLErgodicityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    EnsembleGrowthRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    TimeAverageGrowthRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ErgodicityGap = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    NaiveKellyFraction = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ErgodicityAdjustedKelly = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GrowthRateVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLErgodicityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFeatureConsensusSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FeatureConsensusJson = table.Column<string>(type: "text", nullable: false),
                    ContributingModelCount = table.Column<int>(type: "integer", nullable: false),
                    MeanKendallTau = table.Column<double>(type: "double precision", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFeatureConsensusSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHawkesKernelParams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Mu = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Alpha = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Beta = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", precision: 18, scale: 4, nullable: true),
                    SuppressMultiplier = table.Column<double>(type: "double precision", precision: 5, scale: 2, nullable: false),
                    FitSamples = table.Column<int>(type: "integer", nullable: false),
                    FittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHawkesKernelParams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLKellyFractionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    KellyFraction = table.Column<double>(type: "double precision", nullable: false),
                    HalfKelly = table.Column<double>(type: "double precision", nullable: false),
                    WinRate = table.Column<double>(type: "double precision", nullable: false),
                    WinLossRatio = table.Column<double>(type: "double precision", nullable: false),
                    NegativeEV = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLKellyFractionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLModel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ModelVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DirectionAccuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    MagnitudeRMSE = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModelBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    F1Score = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    ExpectedValue = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    BrierScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    SharpeRatio = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    PlattA = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: true),
                    PlattB = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: true),
                    WalkForwardFolds = table.Column<int>(type: "integer", nullable: false),
                    WalkForwardAvgAccuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    WalkForwardStdDev = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    RegimeScope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IsSuppressed = table.Column<bool>(type: "boolean", nullable: false),
                    IsFallbackChampion = table.Column<bool>(type: "boolean", nullable: false),
                    LiveDirectionAccuracy = table.Column<decimal>(type: "numeric", nullable: true),
                    LiveTotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    LiveActiveDays = table.Column<int>(type: "integer", nullable: false),
                    LearnerArchitecture = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TransferredFromModelId = table.Column<long>(type: "bigint", nullable: true),
                    IsDistilled = table.Column<bool>(type: "boolean", nullable: false),
                    DistilledFromModelId = table.Column<long>(type: "bigint", nullable: true),
                    OnlineLearningUpdateCount = table.Column<int>(type: "integer", nullable: false),
                    LastOnlineLearningAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsMetaLearner = table.Column<bool>(type: "boolean", nullable: false),
                    IsMamlInitializer = table.Column<bool>(type: "boolean", nullable: false),
                    IsSoupModel = table.Column<bool>(type: "boolean", nullable: false),
                    PlattCalibrationDrift = table.Column<double>(type: "double precision", nullable: true),
                    LastChallengedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    PpcSurprised = table.Column<bool>(type: "boolean", nullable: false),
                    LatestOosMaxDrawdown = table.Column<double>(type: "double precision", nullable: true),
                    LatestKyleLambda = table.Column<double>(type: "double precision", nullable: false),
                    IsCvbEnsemble = table.Column<bool>(type: "boolean", nullable: false),
                    PreviousChampionModelId = table.Column<long>(type: "bigint", nullable: true),
                    FragilityScore = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    DatasetHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModel", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModel_MLModel_DistilledFromModelId",
                        column: x => x.DistilledFromModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MLModel_MLModel_PreviousChampionModelId",
                        column: x => x.PreviousChampionModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MLModel_MLModel_TransferredFromModelId",
                        column: x => x.TransferredFromModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MLMrmrFeatureRanking",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MrmrRank = table.Column<int>(type: "integer", nullable: false),
                    MutualInfoWithTarget = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    RedundancyScore = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    MrmrScore = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMrmrFeatureRanking", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPeltChangePointLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChangePointCount = table.Column<int>(type: "integer", nullable: false),
                    ChangePointIndicesJson = table.Column<string>(type: "text", nullable: false),
                    Penalty = table.Column<double>(type: "double precision", nullable: false),
                    TotalCost = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPeltChangePointLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLStackingMetaModel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    BaseModelIdsJson = table.Column<string>(type: "text", nullable: false),
                    BaseModelCount = table.Column<int>(type: "integer", nullable: false),
                    MetaWeightsJson = table.Column<string>(type: "text", nullable: false),
                    MetaBias = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    DirectionAccuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    BrierScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStackingMetaModel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTemperatureScalingLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OptimalTemperature = table.Column<double>(type: "double precision", nullable: false),
                    PreCalibrationEce = table.Column<double>(type: "double precision", nullable: false),
                    PostCalibrationEce = table.Column<double>(type: "double precision", nullable: false),
                    PreCalibrationNll = table.Column<double>(type: "double precision", nullable: false),
                    PostCalibrationNll = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTemperatureScalingLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLVaeEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    LatentDim = table.Column<int>(type: "integer", nullable: false),
                    InputDim = table.Column<int>(type: "integer", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    ReconstructionLoss = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EncoderBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLVaeEncoder", x => x.Id);
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
                name: "Position",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OpenLots = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    AverageEntryPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    UnrealizedPnL = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    RealizedPnL = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Swap = table.Column<decimal>(type: "numeric", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric", nullable: false),
                    StopLoss = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    TakeProfit = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsPaper = table.Column<bool>(type: "boolean", nullable: false),
                    TrailingStopLevel = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    TrailingStopEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TrailingStopType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TrailingStopValue = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    BrokerPositionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OpenOrderId = table.Column<long>(type: "bigint", nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Position", x => x.Id);
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
                name: "RiskProfile",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MaxLotSizePerTrade = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxDailyDrawdownPct = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxTotalDrawdownPct = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxOpenPositions = table.Column<int>(type: "integer", nullable: false),
                    MaxDailyTrades = table.Column<int>(type: "integer", nullable: false),
                    MaxRiskPerTradePct = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxSymbolExposurePct = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxTotalExposurePct = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    RequireStopLoss = table.Column<bool>(type: "boolean", nullable: false),
                    MaxAbsoluteRiskPerTrade = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    DrawdownRecoveryThresholdPct = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    RecoveryLotSizeMultiplier = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    RecoveryExitThresholdPct = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MinStopLossDistancePips = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MinRiskRewardRatio = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxPositionsPerSymbol = table.Column<int>(type: "integer", nullable: false),
                    MaxConsecutiveLosses = table.Column<int>(type: "integer", nullable: false),
                    WeekendGapRiskMultiplier = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxCorrelatedPositions = table.Column<int>(type: "integer", nullable: false),
                    MinEquityFloor = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskProfile", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentimentSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SentimentScore = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    RawDataJson = table.Column<string>(type: "text", nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentimentSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpreadProfiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HourUtc = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: true),
                    SessionName = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SpreadP25 = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SpreadP50 = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SpreadP75 = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SpreadP95 = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SpreadMean = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    AggregatedFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AggregatedTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpreadProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyGenerationCheckpoint",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CycleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CycleDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    UsedRestartSafeFallback = table.Column<bool>(type: "boolean", nullable: false),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationCheckpoint", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyGenerationCycleRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CycleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<double>(type: "double precision", nullable: true),
                    CandidatesCreated = table.Column<int>(type: "integer", nullable: false),
                    ReserveCandidatesCreated = table.Column<int>(type: "integer", nullable: false),
                    CandidatesScreened = table.Column<int>(type: "integer", nullable: false),
                    SymbolsProcessed = table.Column<int>(type: "integer", nullable: false),
                    SymbolsSkipped = table.Column<int>(type: "integer", nullable: false),
                    StrategiesPruned = table.Column<int>(type: "integer", nullable: false),
                    PortfolioFilterRemoved = table.Column<int>(type: "integer", nullable: false),
                    SummaryEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    SummaryEventPayloadJson = table.Column<string>(type: "text", nullable: true),
                    SummaryEventDispatchAttempts = table.Column<int>(type: "integer", nullable: false),
                    SummaryEventDispatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SummaryEventFailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SummaryEventFailureMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FailureStage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationCycleRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyGenerationFailure",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CandidateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CycleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CandidateHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StrategyType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    FailureStage = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DetailsJson = table.Column<string>(type: "text", nullable: true),
                    IsReported = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationFailure", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyGenerationFeedbackState",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StateKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationFeedbackState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyGenerationPendingArtifact",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CycleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CandidatePayloadJson = table.Column<string>(type: "text", nullable: false),
                    NeedsCreationAudit = table.Column<bool>(type: "boolean", nullable: false),
                    NeedsCreatedEvent = table.Column<bool>(type: "boolean", nullable: false),
                    NeedsAutoPromoteEvent = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreationAuditLoggedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CandidateCreatedEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    CandidateCreatedEventDispatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AutoPromotedEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    AutoPromotedEventDispatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QuarantinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TerminalFailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationPendingArtifact", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyGenerationScheduleState",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastRunDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CircuitBreakerUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    RetriesThisWindow = table.Column<int>(type: "integer", nullable: false),
                    RetryWindowDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyGenerationScheduleState", x => x.Id);
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
                name: "TradingAccount",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BrokerServer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BrokerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Leverage = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AccountType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MarginMode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EncryptedPassword = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EncryptedApiKey = table.Column<string>(type: "text", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Equity = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MarginUsed = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MarginAvailable = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MarginLevel = table.Column<decimal>(type: "numeric", nullable: false),
                    Profit = table.Column<decimal>(type: "numeric", nullable: false),
                    Credit = table.Column<decimal>(type: "numeric", nullable: false),
                    MarginSoMode = table.Column<string>(type: "text", nullable: false),
                    MarginSoCall = table.Column<decimal>(type: "numeric", nullable: false),
                    MarginSoStopOut = table.Column<decimal>(type: "numeric", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    MaxAbsoluteDailyLoss = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsPaper = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingAccount", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradingSessionSchedules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SessionName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OpenTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    CloseTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    DayOfWeekStart = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeekEnd = table.Column<int>(type: "integer", nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingSessionSchedules", x => x.Id);
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
                    LastQueueLatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    QueueLatencyP50Ms = table.Column<long>(type: "bigint", nullable: false),
                    QueueLatencyP95Ms = table.Column<long>(type: "bigint", nullable: false),
                    LastExecutionDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionDurationP50Ms = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionDurationP95Ms = table.Column<long>(type: "bigint", nullable: false),
                    RetriesLastHour = table.Column<int>(type: "integer", nullable: false),
                    RecoveriesLastHour = table.Column<int>(type: "integer", nullable: false),
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
                name: "AlertDispatchLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    DispatchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertDispatchLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertDispatchLog_Alert_AlertId",
                        column: x => x.AlertId,
                        principalTable: "Alert",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLAdwinDriftLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    Window1Mean = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Window2Mean = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EpsilonCut = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Window1Size = table.Column<int>(type: "integer", nullable: false),
                    Window2Size = table.Column<int>(type: "integer", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdwinDriftLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLAdwinDriftLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLCausalFeatureAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FeatureIndex = table.Column<int>(type: "integer", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GrangerFStat = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    GrangerPValue = table.Column<decimal>(type: "numeric(10,8)", precision: 10, scale: 8, nullable: false),
                    LagOrder = table.Column<int>(type: "integer", nullable: false),
                    IsCausal = table.Column<bool>(type: "boolean", nullable: false),
                    IsMaskedForTraining = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCausalFeatureAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCausalFeatureAudit_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLConformalBreakerLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ConsecutivePoorCoverageBars = table.Column<int>(type: "integer", nullable: false),
                    EmpiricalCoverage = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    SuspensionBars = table.Column<int>(type: "integer", nullable: false),
                    SuspendedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResumeAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConformalBreakerLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLConformalBreakerLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLConformalCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    NonConformityScoresJson = table.Column<string>(type: "text", nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    CoverageAlpha = table.Column<double>(type: "double precision", precision: 5, scale: 4, nullable: false),
                    CoverageThreshold = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    EmpiricalCoverage = table.Column<double>(type: "double precision", precision: 5, scale: 4, nullable: true),
                    AmbiguousRate = table.Column<double>(type: "double precision", precision: 5, scale: 4, nullable: true),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConformalCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLConformalCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLFeatureInteractionAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FeatureIndexA = table.Column<int>(type: "integer", nullable: false),
                    FeatureNameA = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FeatureIndexB = table.Column<int>(type: "integer", nullable: false),
                    FeatureNameB = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    InteractionScore = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    IsIncludedAsFeature = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFeatureInteractionAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLFeatureInteractionAudit_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLFeatureStalenessLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Lag1Autocorr = table.Column<double>(type: "double precision", nullable: false),
                    IsStale = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFeatureStalenessLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLFeatureStalenessLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLModelEwmaAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    EwmaAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    LastPredictionAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelEwmaAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelEwmaAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLModelHorizonAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HorizonBars = table.Column<int>(type: "integer", nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelHorizonAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelHorizonAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLModelHourlyAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HourUtc = table.Column<int>(type: "integer", nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelHourlyAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelHourlyAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "MLModelRegimeAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Regime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelRegimeAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelRegimeAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLModelSessionAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Session = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelSessionAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelSessionAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLModelVolatilityAccuracy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    VolatilityBucket = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    AtrThresholdLow = table.Column<decimal>(type: "numeric", nullable: false),
                    AtrThresholdHigh = table.Column<decimal>(type: "numeric", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelVolatilityAccuracy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelVolatilityAccuracy_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLShadowEvaluation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChallengerModelId = table.Column<long>(type: "bigint", nullable: false),
                    ChampionModelId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequiredTrades = table.Column<int>(type: "integer", nullable: false),
                    CompletedTrades = table.Column<int>(type: "integer", nullable: false),
                    ChampionDirectionAccuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    ChampionMagnitudeCorrelation = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    ChampionBrierScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    ChallengerDirectionAccuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    ChallengerMagnitudeCorrelation = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    ChallengerBrierScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    PromotionDecision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PromotionThreshold = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    TournamentGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    TournamentRank = table.Column<int>(type: "integer", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLShadowEvaluation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLShadowEvaluation_MLModel_ChallengerModelId",
                        column: x => x.ChallengerModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MLShadowEvaluation_MLModel_ChampionModelId",
                        column: x => x.ChampionModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLTrainingRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TriggerType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FromDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ToDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TotalSamples = table.Column<int>(type: "integer", nullable: false),
                    DirectionAccuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    MagnitudeRMSE = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    MLModelId = table.Column<long>(type: "bigint", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextRetryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PickedUpAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkerInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    TrainingDurationMs = table.Column<long>(type: "bigint", nullable: true),
                    F1Score = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    ExpectedValue = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    BrierScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    SharpeRatio = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    HyperparamConfigJson = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LabelImbalanceRatio = table.Column<decimal>(type: "numeric", nullable: true),
                    TrainingDatasetStatsJson = table.Column<string>(type: "text", nullable: true),
                    LearnerArchitecture = table.Column<int>(type: "integer", nullable: false),
                    IsPretrainingRun = table.Column<bool>(type: "boolean", nullable: false),
                    IsDistillationRun = table.Column<bool>(type: "boolean", nullable: false),
                    IsEmergencyRetrain = table.Column<bool>(type: "boolean", nullable: false),
                    CurriculumFinalDifficulty = table.Column<decimal>(type: "numeric", nullable: true),
                    TemporalDecayHalfLifeDays = table.Column<double>(type: "double precision", nullable: true),
                    SmoteApplied = table.Column<bool>(type: "boolean", nullable: false),
                    AdversarialAugmentApplied = table.Column<bool>(type: "boolean", nullable: false),
                    LabelNoiseRatePercent = table.Column<double>(type: "double precision", nullable: true),
                    SparsityPercent = table.Column<double>(type: "double precision", nullable: true),
                    IsMamlRun = table.Column<bool>(type: "boolean", nullable: false),
                    MamlInnerSteps = table.Column<int>(type: "integer", nullable: true),
                    CoresetSelectionRatio = table.Column<double>(type: "double precision", nullable: true),
                    RareEventWeightingApplied = table.Column<bool>(type: "boolean", nullable: false),
                    CvFoldScoresJson = table.Column<string>(type: "text", nullable: true),
                    NceLossUsed = table.Column<bool>(type: "boolean", nullable: false),
                    MixupApplied = table.Column<bool>(type: "boolean", nullable: false),
                    CurriculumApplied = table.Column<bool>(type: "boolean", nullable: false),
                    DriftTriggerType = table.Column<string>(type: "text", nullable: true),
                    DriftMetadataJson = table.Column<string>(type: "text", nullable: true),
                    AbstentionRate = table.Column<decimal>(type: "numeric", nullable: true),
                    AbstentionPrecision = table.Column<decimal>(type: "numeric", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    DatasetHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CandleIdRangeStart = table.Column<long>(type: "bigint", nullable: true),
                    CandleIdRangeEnd = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTrainingRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLTrainingRun_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PositionLifecycleEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PositionId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    PreviousLots = table.Column<decimal>(type: "numeric", nullable: true),
                    NewLots = table.Column<decimal>(type: "numeric", nullable: true),
                    SwapAccumulated = table.Column<decimal>(type: "numeric", nullable: true),
                    CommissionAccumulated = table.Column<decimal>(type: "numeric", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionLifecycleEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PositionLifecycleEvent_Position_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Position",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Strategy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    StrategyType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PauseReason = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    RiskProfileId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LifecycleStage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LifecycleStageEnteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EstimatedCapacityLots = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    RolloutPct = table.Column<int>(type: "integer", nullable: true),
                    RollbackParametersJson = table.Column<string>(type: "text", nullable: true),
                    RolloutStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RolloutOptimizationRunId = table.Column<long>(type: "bigint", nullable: true),
                    RolloutEvaluationFailureCount = table.Column<int>(type: "integer", nullable: false),
                    RolloutLastFailureMessage = table.Column<string>(type: "text", nullable: true),
                    ScreeningMetricsJson = table.Column<string>(type: "text", nullable: true),
                    PrunedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GenerationCycleId = table.Column<string>(type: "text", nullable: true),
                    GenerationCandidateId = table.Column<string>(type: "text", nullable: true),
                    ValidationPriority = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Strategy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Strategy_RiskProfile_RiskProfileId",
                        column: x => x.RiskProfileId,
                        principalTable: "RiskProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

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
                name: "EAInstance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<string>(type: "text", nullable: false),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Symbols = table.Column<string>(type: "text", nullable: false),
                    ChartSymbol = table.Column<string>(type: "text", nullable: false),
                    ChartTimeframe = table.Column<string>(type: "text", nullable: false),
                    IsCoordinator = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastHeartbeat = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EAVersion = table.Column<string>(type: "text", nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeregisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastProcessedDeltaSequence = table.Column<long>(type: "bigint", nullable: true),
                    LastProcessedPositionSnapshotSequence = table.Column<long>(type: "bigint", nullable: true),
                    LastProcessedOrderSnapshotSequence = table.Column<long>(type: "bigint", nullable: true),
                    LastProcessedDealSnapshotSequence = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    TradingAccountId1 = table.Column<long>(type: "bigint", nullable: true),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EAInstance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EAInstance_TradingAccount_TradingAccountId",
                        column: x => x.TradingAccountId,
                        principalTable: "TradingAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EAInstance_TradingAccount_TradingAccountId1",
                        column: x => x.TradingAccountId1,
                        principalTable: "TradingAccount",
                        principalColumn: "Id");
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

            migrationBuilder.CreateTable(
                name: "MLShadowRegimeBreakdown",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShadowEvaluationId = table.Column<long>(type: "bigint", nullable: false),
                    Regime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    ChampionAccuracy = table.Column<decimal>(type: "numeric(8,6)", nullable: false),
                    ChallengerAccuracy = table.Column<decimal>(type: "numeric(8,6)", nullable: false),
                    AccuracyDelta = table.Column<decimal>(type: "numeric(8,6)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLShadowRegimeBreakdown", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLShadowRegimeBreakdown_MLShadowEvaluation_ShadowEvaluation~",
                        column: x => x.ShadowEvaluationId,
                        principalTable: "MLShadowEvaluation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BacktestRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FromDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ToDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InitialBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailureDetailsJson = table.Column<string>(type: "text", nullable: true),
                    QueueSource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvailableAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimedByWorkerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExecutionStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastHeartbeatAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutionLeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutionLeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    SourceOptimizationRunId = table.Column<long>(type: "bigint", nullable: true),
                    ParametersSnapshotJson = table.Column<string>(type: "text", nullable: true),
                    StrategySnapshotJson = table.Column<string>(type: "text", nullable: true),
                    BacktestOptionsSnapshotJson = table.Column<string>(type: "text", nullable: true),
                    ValidationQueueKey = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    TotalTrades = table.Column<int>(type: "integer", nullable: true),
                    WinRate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    ProfitFactor = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    MaxDrawdownPct = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    SharpeRatio = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    FinalBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    TotalReturn = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestRun", x => x.Id);
                    table.CheckConstraint("CK_BacktestRun_CompletedRequiresCompletedAt", "\"Status\" <> 'Completed' OR \"CompletedAt\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_BacktestRun_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OptimizationRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    TriggerType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Iterations = table.Column<int>(type: "integer", nullable: false),
                    BestParametersJson = table.Column<string>(type: "text", nullable: true),
                    BestHealthScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    BestSharpeRatio = table.Column<decimal>(type: "numeric", nullable: true),
                    BestMaxDrawdownPct = table.Column<decimal>(type: "numeric", nullable: true),
                    BestWinRate = table.Column<decimal>(type: "numeric", nullable: true),
                    BaselineParametersJson = table.Column<string>(type: "text", nullable: true),
                    BaselineHealthScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    ConfigSnapshotJson = table.Column<string>(type: "text", nullable: true),
                    RunMetadataJson = table.Column<string>(type: "text", nullable: true),
                    IntermediateResultsJson = table.Column<string>(type: "text", nullable: true),
                    CheckpointVersion = table.Column<int>(type: "integer", nullable: false),
                    ApprovalReportJson = table.Column<string>(type: "text", nullable: true),
                    DeterministicSeed = table.Column<int>(type: "integer", nullable: false),
                    DeterministicSeedVersion = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    FailureCategory = table.Column<int>(type: "integer", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastHeartbeatAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutionLeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutionLeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    DeferralReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    DeferredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeferralCount = table.Column<int>(type: "integer", nullable: false),
                    LastResumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeferredUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutionStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutionStage = table.Column<int>(type: "integer", nullable: false),
                    ExecutionStageMessage = table.Column<string>(type: "text", nullable: true),
                    ExecutionStageUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastOperationalIssueCode = table.Column<string>(type: "text", nullable: true),
                    LastOperationalIssueMessage = table.Column<string>(type: "text", nullable: true),
                    LastOperationalIssueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResultsPersistedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovalEvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidationFollowUpsCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidationFollowUpStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    NextFollowUpCheckAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FollowUpLastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FollowUpRepairAttempts = table.Column<int>(type: "integer", nullable: false),
                    FollowUpLastStatusCode = table.Column<string>(type: "text", nullable: true),
                    FollowUpLastStatusMessage = table.Column<string>(type: "text", nullable: true),
                    FollowUpStatusUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletionPublicationPayloadJson = table.Column<string>(type: "text", nullable: true),
                    CompletionPublicationStatus = table.Column<int>(type: "integer", nullable: true),
                    CompletionPublicationAttempts = table.Column<int>(type: "integer", nullable: false),
                    CompletionPublicationLastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletionPublicationPreparedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletionPublicationCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletionPublicationErrorMessage = table.Column<string>(type: "text", nullable: true),
                    LifecycleReconciledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptimizationRun", x => x.Id);
                    table.CheckConstraint("CK_OptimizationRun_ApprovalStatesRequireApprovalEvaluated", "\"Status\" NOT IN ('Approved','Rejected') OR \"ApprovalEvaluatedAt\" IS NOT NULL");
                    table.CheckConstraint("CK_OptimizationRun_CompletionPreparedRequiresPayload", "\"CompletionPublicationPreparedAt\" IS NULL OR \"CompletionPublicationPayloadJson\" IS NOT NULL");
                    table.CheckConstraint("CK_OptimizationRun_CompletionPublishedRequiresPreparedPayload", "\"CompletionPublicationStatus\" IS DISTINCT FROM 1 OR (\"CompletionPublicationPayloadJson\" IS NOT NULL AND \"CompletionPublicationPreparedAt\" IS NOT NULL AND \"CompletionPublicationCompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_OptimizationRun_FollowUpStatusRequiresCreation", "\"ValidationFollowUpStatus\" IS NULL OR \"ValidationFollowUpsCreatedAt\" IS NOT NULL");
                    table.CheckConstraint("CK_OptimizationRun_TerminalRunsRequireResultsPersisted", "\"Status\" NOT IN ('Completed','Approved','Rejected') OR \"ResultsPersistedAt\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_OptimizationRun_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StrategyAllocation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    RollingSharpRatio = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    LastRebalancedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyAllocation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrategyAllocation_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "StrategyPerformanceSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    WindowTrades = table.Column<int>(type: "integer", nullable: false),
                    WinningTrades = table.Column<int>(type: "integer", nullable: false),
                    LosingTrades = table.Column<int>(type: "integer", nullable: false),
                    WinRate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    ProfitFactor = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SharpeRatio = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    MaxDrawdownPct = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TotalPnL = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    HealthScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    HealthStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyPerformanceSnapshot", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrategyPerformanceSnapshot_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "TradeSignal",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    StopLoss = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    TakeProfit = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    PartialTakeProfit = table.Column<decimal>(type: "numeric", nullable: true),
                    PartialClosePercent = table.Column<decimal>(type: "numeric", nullable: true),
                    SuggestedLotSize = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    MLPredictedDirection = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    MLPredictedMagnitude = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    MLConfidenceScore = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    MLModelId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RejectionReason = table.Column<string>(type: "text", nullable: true),
                    OrderId = table.Column<long>(type: "bigint", nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeSignal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradeSignal_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TradeSignal_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WalkForwardRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FromDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ToDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InSampleDays = table.Column<int>(type: "integer", nullable: false),
                    OutOfSampleDays = table.Column<int>(type: "integer", nullable: false),
                    ReOptimizePerFold = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InitialBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AverageOutOfSampleScore = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    ScoreConsistency = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    WindowResultsJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailureDetailsJson = table.Column<string>(type: "text", nullable: true),
                    QueueSource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvailableAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimedByWorkerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExecutionStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastHeartbeatAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutionLeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutionLeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SourceOptimizationRunId = table.Column<long>(type: "bigint", nullable: true),
                    ParametersSnapshotJson = table.Column<string>(type: "text", nullable: true),
                    StrategySnapshotJson = table.Column<string>(type: "text", nullable: true),
                    BacktestOptionsSnapshotJson = table.Column<string>(type: "text", nullable: true),
                    ValidationQueueKey = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkForwardRun", x => x.Id);
                    table.CheckConstraint("CK_WalkForwardRun_PositiveWindowDays", "\"InSampleDays\" > 0 AND \"OutOfSampleDays\" > 0");
                    table.ForeignKey(
                        name: "FK_WalkForwardRun_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StrategyRegimeParams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Regime = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    HealthScore = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    HealthScoreCILower = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    OptimizationRunId = table.Column<long>(type: "bigint", nullable: true),
                    OptimizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyRegimeParams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrategyRegimeParams_OptimizationRun_OptimizationRunId",
                        column: x => x.OptimizationRunId,
                        principalTable: "OptimizationRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StrategyRegimeParams_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLModelPredictionLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradeSignalId = table.Column<long>(type: "bigint", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ModelRole = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    PredictedDirection = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    PredictedMagnitudePips = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    RawProbability = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    CalibratedProbability = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    ServedCalibratedProbability = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    DecisionThresholdUsed = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    ActualDirection = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    ActualMagnitudePips = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: true),
                    WasProfitable = table.Column<bool>(type: "boolean", nullable: true),
                    DirectionCorrect = table.Column<bool>(type: "boolean", nullable: true),
                    HorizonCorrect3 = table.Column<bool>(type: "boolean", nullable: true),
                    HorizonCorrect6 = table.Column<bool>(type: "boolean", nullable: true),
                    HorizonCorrect12 = table.Column<bool>(type: "boolean", nullable: true),
                    PredictedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OutcomeRecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolutionSource = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    EnsembleDisagreement = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    ContributionsJson = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    CounterfactualJson = table.Column<string>(type: "text", nullable: true),
                    MagnitudeUncertaintyPips = table.Column<decimal>(type: "numeric", nullable: true),
                    McDropoutVariance = table.Column<decimal>(type: "numeric", nullable: true),
                    McDropoutMean = table.Column<decimal>(type: "numeric", nullable: true),
                    ConformalNonConformityScore = table.Column<double>(type: "double precision", nullable: true),
                    ShapValuesJson = table.Column<string>(type: "text", nullable: true),
                    MagnitudeP10Pips = table.Column<decimal>(type: "numeric", nullable: true),
                    MagnitudeP90Pips = table.Column<decimal>(type: "numeric", nullable: true),
                    OodMahalanobisScore = table.Column<double>(type: "double precision", nullable: true),
                    IsOod = table.Column<bool>(type: "boolean", nullable: false),
                    RegimeRoutingDecision = table.Column<string>(type: "text", nullable: true),
                    EstimatedTimeToTargetBars = table.Column<double>(type: "double precision", nullable: true),
                    SurvivalHazardRate = table.Column<double>(type: "double precision", nullable: true),
                    CommitteeModelIdsJson = table.Column<string>(type: "text", nullable: true),
                    CommitteeDisagreement = table.Column<decimal>(type: "numeric", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelPredictionLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelPredictionLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MLModelPredictionLog_TradeSignal_TradeSignalId",
                        column: x => x.TradeSignalId,
                        principalTable: "TradeSignal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Order",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradeSignalId = table.Column<long>(type: "bigint", nullable: true),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Session = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    OrderType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ExecutionType = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    StopLoss = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    TakeProfit = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    FilledPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    FilledQuantity = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BrokerOrderId = table.Column<string>(type: "text", nullable: true),
                    RejectionReason = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    IsPaper = table.Column<bool>(type: "boolean", nullable: false),
                    TrailingStopEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TrailingStopType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TrailingStopValue = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    HighestFavourablePrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    ParentOrderId = table.Column<long>(type: "bigint", nullable: true),
                    ExecutionAlgorithm = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TimeInForce = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MaxSlippagePips = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: true),
                    AlgoSliceCount = table.Column<int>(type: "integer", nullable: true),
                    AlgoDurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    GtdExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FilledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Order", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Order_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Order_TradeSignal_TradeSignalId",
                        column: x => x.TradeSignalId,
                        principalTable: "TradeSignal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Order_TradingAccount_TradingAccountId",
                        column: x => x.TradingAccountId,
                        principalTable: "TradingAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SignalAccountAttempt",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradeSignalId = table.Column<long>(type: "bigint", nullable: false),
                    TradingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    BlockReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalAccountAttempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignalAccountAttempt_TradeSignal_TradeSignalId",
                        column: x => x.TradeSignalId,
                        principalTable: "TradeSignal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SignalAccountAttempt_TradingAccount_TradingAccountId",
                        column: x => x.TradingAccountId,
                        principalTable: "TradingAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionQualityLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    StrategyId = table.Column<long>(type: "bigint", nullable: true),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Session = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequestedPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    FilledPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SlippagePips = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    SubmitToFillMs = table.Column<long>(type: "bigint", nullable: false),
                    WasPartialFill = table.Column<bool>(type: "boolean", nullable: false),
                    FillRate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionQualityLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionQualityLog_Order_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExecutionQualityLog_Strategy_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PositionScaleOrder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PositionId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    ScaleType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScaleStep = table.Column<int>(type: "integer", nullable: false),
                    TriggerPips = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    LotSize = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    TakeProfitPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionScaleOrder", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PositionScaleOrder_Order_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PositionScaleOrder_Position_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Position",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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

            migrationBuilder.CreateIndex(
                name: "IX_AccountPerformanceAttribution_TradingAccountId_AttributionD~",
                table: "AccountPerformanceAttribution",
                columns: new[] { "TradingAccountId", "AttributionDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Alert_Symbol_AlertType_IsActive",
                table: "Alert",
                columns: new[] { "Symbol", "AlertType", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertDispatchLog_AlertId",
                table: "AlertDispatchLog",
                column: "AlertId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequest_OperationType_TargetEntityId_Status",
                table: "ApprovalRequest",
                columns: new[] { "OperationType", "TargetEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_ActiveValidationQueueKey",
                table: "BacktestRun",
                column: "ValidationQueueKey",
                unique: true,
                filter: "\"ValidationQueueKey\" IS NOT NULL AND \"Status\" IN ('Queued','Running') AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_SourceOptimizationRunId",
                table: "BacktestRun",
                column: "SourceOptimizationRunId",
                unique: true,
                filter: "\"SourceOptimizationRunId\" IS NOT NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_Status_AvailableAt_Priority_QueuedAt_Id",
                table: "BacktestRun",
                columns: new[] { "Status", "AvailableAt", "Priority", "QueuedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_Status_ExecutionLeaseExpiresAt",
                table: "BacktestRun",
                columns: new[] { "Status", "ExecutionLeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_StrategyId",
                table: "BacktestRun",
                column: "StrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRun_StrategyId_Status",
                table: "BacktestRun",
                columns: new[] { "StrategyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Candle_Symbol_Timeframe_IsClosed",
                table: "Candle",
                columns: new[] { "Symbol", "Timeframe", "IsClosed" });

            migrationBuilder.CreateIndex(
                name: "IX_Candle_Symbol_Timeframe_Timestamp",
                table: "Candle",
                columns: new[] { "Symbol", "Timeframe", "Timestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_COTReport_Currency_ReportDate",
                table: "COTReport",
                columns: new[] { "Currency", "ReportDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyPair_Symbol",
                table: "CurrencyPair",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterEvent_DeadLetteredAt",
                table: "DeadLetterEvent",
                column: "DeadLetteredAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterEvent_IsResolved",
                table: "DeadLetterEvent",
                column: "IsResolved",
                filter: "\"IsResolved\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionLog_CreatedAt",
                table: "DecisionLog",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionLog_EntityType_EntityId",
                table: "DecisionLog",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_DrawdownSnapshot_RecordedAt",
                table: "DrawdownSnapshot",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EACommand_CreatedAt",
                table: "EACommand",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EACommand_TargetInstanceId_Acknowledged",
                table: "EACommand",
                columns: new[] { "TargetInstanceId", "Acknowledged" },
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_EAInstance_InstanceId",
                table: "EAInstance",
                column: "InstanceId",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_EAInstance_TradingAccountId",
                table: "EAInstance",
                column: "TradingAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_EAInstance_TradingAccountId1",
                table: "EAInstance",
                column: "TradingAccountId1");

            migrationBuilder.CreateIndex(
                name: "IX_EconomicEvent_Currency_ScheduledAt",
                table: "EconomicEvent",
                columns: new[] { "Currency", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EngineConfig_Key",
                table: "EngineConfig",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EngineConfigAuditLog_Key_ChangedAt",
                table: "EngineConfigAuditLog",
                columns: new[] { "Key", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionQualityLog_OrderId",
                table: "ExecutionQualityLog",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionQualityLog_StrategyId",
                table: "ExecutionQualityLog",
                column: "StrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionQualityLog_Symbol_Session",
                table: "ExecutionQualityLog",
                columns: new[] { "Symbol", "Session" });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureVector_CandleId",
                table: "FeatureVector",
                column: "CandleId");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureVector_PointInTime",
                table: "FeatureVector",
                columns: new[] { "Symbol", "Timeframe", "BarTimestamp", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureVector_SchemaEviction",
                table: "FeatureVector",
                columns: new[] { "SchemaHash", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureVector_Symbol_Timeframe_BarTimestamp",
                table: "FeatureVector",
                columns: new[] { "Symbol", "Timeframe", "BarTimestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureVectorLineage_Lookup",
                table: "FeatureVectorLineage",
                columns: new[] { "Symbol", "Timeframe", "SchemaHash" });

            migrationBuilder.CreateIndex(
                name: "IX_LivePrice_Symbol",
                table: "LivePrice",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketDataAnomaly_Symbol_DetectedAt",
                table: "MarketDataAnomaly",
                columns: new[] { "Symbol", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketRegimeSnapshot_Symbol_Timeframe_DetectedAt",
                table: "MarketRegimeSnapshot",
                columns: new[] { "Symbol", "Timeframe", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLAdwinDriftLog_MLModelId_DetectedAt",
                table: "MLAdwinDriftLog",
                columns: new[] { "MLModelId", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCausalFeatureAudit_MLModelId_FeatureIndex",
                table: "MLCausalFeatureAudit",
                columns: new[] { "MLModelId", "FeatureIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCausalFeatureAudit_MLModelId_IsCausal",
                table: "MLCausalFeatureAudit",
                columns: new[] { "MLModelId", "IsCausal" });

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalBreakerLog_MLModelId",
                table: "MLConformalBreakerLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalCalibration_MLModelId_IsDeleted",
                table: "MLConformalCalibration",
                columns: new[] { "MLModelId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalCalibration_Symbol_Timeframe",
                table: "MLConformalCalibration",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCorrelatedFailureLog_DetectedAt",
                table: "MLCorrelatedFailureLog",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLCpcEncoder_Symbol_Timeframe_IsActive",
                table: "MLCpcEncoder",
                columns: new[] { "Symbol", "Timeframe", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_MLErgodicityLogs_MLModelId_ComputedAt",
                table: "MLErgodicityLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureConsensusSnapshot_Symbol_Timeframe_DetectedAt",
                table: "MLFeatureConsensusSnapshot",
                columns: new[] { "Symbol", "Timeframe", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureInteractionAudit_MLModelId_IsIncludedAsFeature",
                table: "MLFeatureInteractionAudit",
                columns: new[] { "MLModelId", "IsIncludedAsFeature" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureInteractionAudit_MLModelId_Rank",
                table: "MLFeatureInteractionAudit",
                columns: new[] { "MLModelId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureStalenessLog_MLModelId_FeatureName",
                table: "MLFeatureStalenessLog",
                columns: new[] { "MLModelId", "FeatureName" });

            migrationBuilder.CreateIndex(
                name: "IX_MLHawkesKernelParams_Symbol_Timeframe_FittedAt",
                table: "MLHawkesKernelParams",
                columns: new[] { "Symbol", "Timeframe", "FittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLKellyFractionLogs_MLModelId",
                table: "MLKellyFractionLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLModel_DistilledFromModelId",
                table: "MLModel",
                column: "DistilledFromModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLModel_PreviousChampionModelId",
                table: "MLModel",
                column: "PreviousChampionModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLModel_Symbol_Timeframe_IsActive",
                table: "MLModel",
                columns: new[] { "Symbol", "Timeframe", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModel_Symbol_Timeframe_RegimeScope_IsActive",
                table: "MLModel",
                columns: new[] { "Symbol", "Timeframe", "RegimeScope", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModel_TransferredFromModelId",
                table: "MLModel",
                column: "TransferredFromModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLModelEwmaAccuracy_MLModelId",
                table: "MLModelEwmaAccuracy",
                column: "MLModelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelEwmaAccuracy_Symbol_Timeframe",
                table: "MLModelEwmaAccuracy",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHorizonAccuracy_MLModelId_HorizonBars",
                table: "MLModelHorizonAccuracy",
                columns: new[] { "MLModelId", "HorizonBars" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHorizonAccuracy_Symbol_Timeframe_HorizonBars",
                table: "MLModelHorizonAccuracy",
                columns: new[] { "Symbol", "Timeframe", "HorizonBars" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHourlyAccuracy_MLModelId_HourUtc",
                table: "MLModelHourlyAccuracy",
                columns: new[] { "MLModelId", "HourUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelHourlyAccuracy_Symbol_Timeframe_HourUtc",
                table: "MLModelHourlyAccuracy",
                columns: new[] { "Symbol", "Timeframe", "HourUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelLifecycleLog_MLModelId_OccurredAt",
                table: "MLModelLifecycleLog",
                columns: new[] { "MLModelId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelPredictionLog_MLModelId_ModelRole",
                table: "MLModelPredictionLog",
                columns: new[] { "MLModelId", "ModelRole" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelPredictionLog_TradeSignalId",
                table: "MLModelPredictionLog",
                column: "TradeSignalId");

            migrationBuilder.CreateIndex(
                name: "IX_MLModelPredictionLog_TradeSignalId_MLModelId",
                table: "MLModelPredictionLog",
                columns: new[] { "TradeSignalId", "MLModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelRegimeAccuracy_MLModelId_Regime",
                table: "MLModelRegimeAccuracy",
                columns: new[] { "MLModelId", "Regime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelRegimeAccuracy_Symbol_Timeframe_Regime",
                table: "MLModelRegimeAccuracy",
                columns: new[] { "Symbol", "Timeframe", "Regime" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelSessionAccuracy_MLModelId_Session",
                table: "MLModelSessionAccuracy",
                columns: new[] { "MLModelId", "Session" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelSessionAccuracy_Symbol_Timeframe_Session",
                table: "MLModelSessionAccuracy",
                columns: new[] { "Symbol", "Timeframe", "Session" });

            migrationBuilder.CreateIndex(
                name: "IX_MLModelVolatilityAccuracy_MLModelId_VolatilityBucket",
                table: "MLModelVolatilityAccuracy",
                columns: new[] { "MLModelId", "VolatilityBucket" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelVolatilityAccuracy_Symbol_Timeframe_VolatilityBucket",
                table: "MLModelVolatilityAccuracy",
                columns: new[] { "Symbol", "Timeframe", "VolatilityBucket" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMrmrFeatureRanking_Symbol_Timeframe_ComputedAt",
                table: "MLMrmrFeatureRanking",
                columns: new[] { "Symbol", "Timeframe", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMrmrFeatureRanking_Symbol_Timeframe_MrmrRank",
                table: "MLMrmrFeatureRanking",
                columns: new[] { "Symbol", "Timeframe", "MrmrRank" });

            migrationBuilder.CreateIndex(
                name: "IX_MLPeltChangePointLogs_MLModelId",
                table: "MLPeltChangePointLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLShadowEvaluation_ChallengerModelId",
                table: "MLShadowEvaluation",
                column: "ChallengerModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLShadowEvaluation_ChampionModelId",
                table: "MLShadowEvaluation",
                column: "ChampionModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLShadowEvaluation_Symbol_Timeframe_Status",
                table: "MLShadowEvaluation",
                columns: new[] { "Symbol", "Timeframe", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MLShadowRegimeBreakdown_ShadowEvaluationId_Regime",
                table: "MLShadowRegimeBreakdown",
                columns: new[] { "ShadowEvaluationId", "Regime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLStackingMetaModel_Symbol_Timeframe_IsActive",
                table: "MLStackingMetaModel",
                columns: new[] { "Symbol", "Timeframe", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_MLTemperatureScalingLogs_MLModelId",
                table: "MLTemperatureScalingLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTrainingRun_MLModelId",
                table: "MLTrainingRun",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTrainingRun_Symbol_Timeframe_Status",
                table: "MLTrainingRun",
                columns: new[] { "Symbol", "Timeframe", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MLVaeEncoder_Symbol_Timeframe_IsActive",
                table: "MLVaeEncoder",
                columns: new[] { "Symbol", "Timeframe", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_ActivePerStrategy",
                table: "OptimizationRun",
                column: "StrategyId",
                unique: true,
                filter: "\"Status\" IN ('Queued','Running') AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_LastResumedAtUtc",
                table: "OptimizationRun",
                column: "LastResumedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_DeferralCount_DeferredUntilUtc",
                table: "OptimizationRun",
                columns: new[] { "Status", "DeferralCount", "DeferredUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_DeferralReason_DeferredUntilUtc",
                table: "OptimizationRun",
                columns: new[] { "Status", "DeferralReason", "DeferredUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_DeferredUntilUtc",
                table: "OptimizationRun",
                columns: new[] { "Status", "DeferredUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_ExecutionLeaseExpiresAt",
                table: "OptimizationRun",
                columns: new[] { "Status", "ExecutionLeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_StrategyId_Status",
                table: "OptimizationRun",
                columns: new[] { "StrategyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_ValidationFollowUpsCreatedAt",
                table: "OptimizationRun",
                column: "ValidationFollowUpsCreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Order_StrategyId",
                table: "Order",
                column: "StrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_Order_Symbol_Status",
                table: "Order",
                columns: new[] { "Symbol", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Order_TradeSignalId",
                table: "Order",
                column: "TradeSignalId");

            migrationBuilder.CreateIndex(
                name: "IX_Order_TradingAccountId",
                table: "Order",
                column: "TradingAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderBookSnapshot_Symbol_CapturedAt",
                table: "OrderBookSnapshot",
                columns: new[] { "Symbol", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Position_OpenOrderId",
                table: "Position",
                column: "OpenOrderId",
                unique: true,
                filter: "\"OpenOrderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Position_Symbol_Status",
                table: "Position",
                columns: new[] { "Symbol", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PositionLifecycleEvent_PositionId",
                table: "PositionLifecycleEvent",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_PositionScaleOrder_OrderId",
                table: "PositionScaleOrder",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PositionScaleOrder_PositionId",
                table: "PositionScaleOrder",
                column: "PositionId");

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
                name: "IX_RiskProfile_IsDefault",
                table: "RiskProfile",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_SentimentSnapshot_Currency_CapturedAt",
                table: "SentimentSnapshot",
                columns: new[] { "Currency", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SignalAccountAttempt_TradeSignalId_TradingAccountId",
                table: "SignalAccountAttempt",
                columns: new[] { "TradeSignalId", "TradingAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignalAccountAttempt_TradingAccountId",
                table: "SignalAccountAttempt",
                column: "TradingAccountId");

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
                name: "IX_SpreadProfile_Symbol_Hour_DOW",
                table: "SpreadProfiles",
                columns: new[] { "Symbol", "HourUtc", "DayOfWeek" });

            migrationBuilder.CreateIndex(
                name: "IX_SpreadProfiles_Symbol",
                table: "SpreadProfiles",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_Strategy_ActiveGenerationKey",
                table: "Strategy",
                columns: new[] { "StrategyType", "Symbol", "Timeframe" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Strategy_RiskProfileId",
                table: "Strategy",
                column: "RiskProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Strategy_Symbol",
                table: "Strategy",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyAllocation_StrategyId",
                table: "StrategyAllocation",
                column: "StrategyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StrategyCapacity_StrategyId_EstimatedAt",
                table: "StrategyCapacity",
                columns: new[] { "StrategyId", "EstimatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationCheckpoint_WorkerName",
                table: "StrategyGenerationCheckpoint",
                column: "WorkerName",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationCycleRun_CycleId",
                table: "StrategyGenerationCycleRun",
                column: "CycleId",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationCycleRun_WorkerName_StartedAtUtc_IsDeleted",
                table: "StrategyGenerationCycleRun",
                columns: new[] { "WorkerName", "StartedAtUtc", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationFailure_CandidateId_FailureStage_Resolved~",
                table: "StrategyGenerationFailure",
                columns: new[] { "CandidateId", "FailureStage", "ResolvedAtUtc", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationFailure_IsReported_ResolvedAtUtc_IsDeleted",
                table: "StrategyGenerationFailure",
                columns: new[] { "IsReported", "ResolvedAtUtc", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationFeedbackState_StateKey_IsDeleted",
                table: "StrategyGenerationFeedbackState",
                columns: new[] { "StateKey", "IsDeleted" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationPendingArtifact_CandidateId",
                table: "StrategyGenerationPendingArtifact",
                column: "CandidateId",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationPendingArtifact_StrategyId",
                table: "StrategyGenerationPendingArtifact",
                column: "StrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyGenerationScheduleState_WorkerName",
                table: "StrategyGenerationScheduleState",
                column: "WorkerName",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyPerformanceSnapshot_EvaluatedAt",
                table: "StrategyPerformanceSnapshot",
                column: "EvaluatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyPerformanceSnapshot_StrategyId",
                table: "StrategyPerformanceSnapshot",
                column: "StrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyRegimeParams_OptimizationRunId",
                table: "StrategyRegimeParams",
                column: "OptimizationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyRegimeParams_StrategyId_Regime",
                table: "StrategyRegimeParams",
                columns: new[] { "StrategyId", "Regime" },
                unique: true,
                filter: "\"IsDeleted\" = false");

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
                name: "IX_TradeSignal_MLModelId",
                table: "TradeSignal",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeSignal_StrategyId_Status",
                table: "TradeSignal",
                columns: new[] { "StrategyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TradeSignal_Symbol_Status",
                table: "TradeSignal",
                columns: new[] { "Symbol", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingAccount_AccountId_BrokerServer",
                table: "TradingAccount",
                columns: new[] { "AccountId", "BrokerServer" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_TradingAccount_IsActive",
                table: "TradingAccount",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TradingSessionSchedule_Symbol_Session_Instance",
                table: "TradingSessionSchedules",
                columns: new[] { "Symbol", "SessionName", "InstanceId" });

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
                name: "IX_WalkForwardRun_ActiveValidationQueueKey",
                table: "WalkForwardRun",
                column: "ValidationQueueKey",
                unique: true,
                filter: "\"ValidationQueueKey\" IS NOT NULL AND \"Status\" IN ('Queued','Running') AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_SourceOptimizationRunId",
                table: "WalkForwardRun",
                column: "SourceOptimizationRunId",
                unique: true,
                filter: "\"SourceOptimizationRunId\" IS NOT NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_Status_AvailableAt_Priority_QueuedAt_Id",
                table: "WalkForwardRun",
                columns: new[] { "Status", "AvailableAt", "Priority", "QueuedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_Status_ExecutionLeaseExpiresAt",
                table: "WalkForwardRun",
                columns: new[] { "Status", "ExecutionLeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_StrategyId",
                table: "WalkForwardRun",
                column: "StrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardRun_StrategyId_Status",
                table: "WalkForwardRun",
                columns: new[] { "StrategyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHealthSnapshot_WorkerName_CapturedAt",
                table: "WorkerHealthSnapshot",
                columns: new[] { "WorkerName", "CapturedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountPerformanceAttribution");

            migrationBuilder.DropTable(
                name: "AlertDispatchLog");

            migrationBuilder.DropTable(
                name: "ApprovalRequest");

            migrationBuilder.DropTable(
                name: "BacktestRun");

            migrationBuilder.DropTable(
                name: "BrokerAccountSnapshot");

            migrationBuilder.DropTable(
                name: "Candle");

            migrationBuilder.DropTable(
                name: "COTReport");

            migrationBuilder.DropTable(
                name: "CurrencyPair");

            migrationBuilder.DropTable(
                name: "DeadLetterEvent");

            migrationBuilder.DropTable(
                name: "DecisionLog");

            migrationBuilder.DropTable(
                name: "DrawdownSnapshot");

            migrationBuilder.DropTable(
                name: "EACommand");

            migrationBuilder.DropTable(
                name: "EAInstance");

            migrationBuilder.DropTable(
                name: "EconomicEvent");

            migrationBuilder.DropTable(
                name: "EngineConfig");

            migrationBuilder.DropTable(
                name: "EngineConfigAuditLog");

            migrationBuilder.DropTable(
                name: "ExecutionQualityLog");

            migrationBuilder.DropTable(
                name: "FeatureVector");

            migrationBuilder.DropTable(
                name: "FeatureVectorLineage");

            migrationBuilder.DropTable(
                name: "LivePrice");

            migrationBuilder.DropTable(
                name: "MarketDataAnomaly");

            migrationBuilder.DropTable(
                name: "MarketRegimeSnapshot");

            migrationBuilder.DropTable(
                name: "MLAdwinDriftLog");

            migrationBuilder.DropTable(
                name: "MLCausalFeatureAudit");

            migrationBuilder.DropTable(
                name: "MLConformalBreakerLog");

            migrationBuilder.DropTable(
                name: "MLConformalCalibration");

            migrationBuilder.DropTable(
                name: "MLCorrelatedFailureLog");

            migrationBuilder.DropTable(
                name: "MLCpcEncoder");

            migrationBuilder.DropTable(
                name: "MLErgodicityLogs");

            migrationBuilder.DropTable(
                name: "MLFeatureConsensusSnapshot");

            migrationBuilder.DropTable(
                name: "MLFeatureInteractionAudit");

            migrationBuilder.DropTable(
                name: "MLFeatureStalenessLog");

            migrationBuilder.DropTable(
                name: "MLHawkesKernelParams");

            migrationBuilder.DropTable(
                name: "MLKellyFractionLogs");

            migrationBuilder.DropTable(
                name: "MLModelEwmaAccuracy");

            migrationBuilder.DropTable(
                name: "MLModelHorizonAccuracy");

            migrationBuilder.DropTable(
                name: "MLModelHourlyAccuracy");

            migrationBuilder.DropTable(
                name: "MLModelLifecycleLog");

            migrationBuilder.DropTable(
                name: "MLModelPredictionLog");

            migrationBuilder.DropTable(
                name: "MLModelRegimeAccuracy");

            migrationBuilder.DropTable(
                name: "MLModelSessionAccuracy");

            migrationBuilder.DropTable(
                name: "MLModelVolatilityAccuracy");

            migrationBuilder.DropTable(
                name: "MLMrmrFeatureRanking");

            migrationBuilder.DropTable(
                name: "MLPeltChangePointLogs");

            migrationBuilder.DropTable(
                name: "MLShadowRegimeBreakdown");

            migrationBuilder.DropTable(
                name: "MLStackingMetaModel");

            migrationBuilder.DropTable(
                name: "MLTemperatureScalingLogs");

            migrationBuilder.DropTable(
                name: "MLTrainingRun");

            migrationBuilder.DropTable(
                name: "MLVaeEncoder");

            migrationBuilder.DropTable(
                name: "OrderBookSnapshot");

            migrationBuilder.DropTable(
                name: "PositionLifecycleEvent");

            migrationBuilder.DropTable(
                name: "PositionScaleOrder");

            migrationBuilder.DropTable(
                name: "ProcessedIdempotencyKey");

            migrationBuilder.DropTable(
                name: "SentimentSnapshot");

            migrationBuilder.DropTable(
                name: "SignalAccountAttempt");

            migrationBuilder.DropTable(
                name: "SignalAllocation");

            migrationBuilder.DropTable(
                name: "SpreadProfiles");

            migrationBuilder.DropTable(
                name: "StrategyAllocation");

            migrationBuilder.DropTable(
                name: "StrategyCapacity");

            migrationBuilder.DropTable(
                name: "StrategyGenerationCheckpoint");

            migrationBuilder.DropTable(
                name: "StrategyGenerationCycleRun");

            migrationBuilder.DropTable(
                name: "StrategyGenerationFailure");

            migrationBuilder.DropTable(
                name: "StrategyGenerationFeedbackState");

            migrationBuilder.DropTable(
                name: "StrategyGenerationPendingArtifact");

            migrationBuilder.DropTable(
                name: "StrategyGenerationScheduleState");

            migrationBuilder.DropTable(
                name: "StrategyPerformanceSnapshot");

            migrationBuilder.DropTable(
                name: "StrategyRegimeParams");

            migrationBuilder.DropTable(
                name: "StrategyVariant");

            migrationBuilder.DropTable(
                name: "StressTestResult");

            migrationBuilder.DropTable(
                name: "TickRecord");

            migrationBuilder.DropTable(
                name: "TradeRationale");

            migrationBuilder.DropTable(
                name: "TradingSessionSchedules");

            migrationBuilder.DropTable(
                name: "TransactionCostAnalysis");

            migrationBuilder.DropTable(
                name: "WalkForwardRun");

            migrationBuilder.DropTable(
                name: "WorkerHealthSnapshot");

            migrationBuilder.DropTable(
                name: "Alert");

            migrationBuilder.DropTable(
                name: "MLShadowEvaluation");

            migrationBuilder.DropTable(
                name: "Position");

            migrationBuilder.DropTable(
                name: "OptimizationRun");

            migrationBuilder.DropTable(
                name: "StressTestScenario");

            migrationBuilder.DropTable(
                name: "Order");

            migrationBuilder.DropTable(
                name: "TradeSignal");

            migrationBuilder.DropTable(
                name: "TradingAccount");

            migrationBuilder.DropTable(
                name: "MLModel");

            migrationBuilder.DropTable(
                name: "Strategy");

            migrationBuilder.DropTable(
                name: "RiskProfile");
        }
    }
}
