using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Infrastructure.Persistence;

/// <summary>
/// Seeds the database with the minimum data required to run the engine locally.
/// Trading accounts are created at runtime when the EA registers via POST /auth/register.
/// Idempotent — checks for existing rows before inserting.
/// </summary>
public sealed class DatabaseSeeder
{
    private readonly IWriteApplicationDbContext _context;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(IWriteApplicationDbContext context, ILogger<DatabaseSeeder> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var db = _context.GetDbContext();

        await SeedCurrencyPairsAsync(db, ct);
        await SeedTradingAccountAsync(db, ct);
        await SeedRiskProfileAsync(db, ct);
        await SeedStrategiesAsync(db, ct);
        await SeedLivePricesAsync(db, ct);
        await SeedEngineConfigAsync(db, ct);
        await SeedInitialMLTrainingRunsAsync(db, ct);

        _logger.LogInformation("Database seeding completed");
    }

    private async Task SeedCurrencyPairsAsync(DbContext db, CancellationToken ct)
    {
        var pairs = db.Set<CurrencyPair>();
        if (await pairs.AnyAsync(ct)) return;

        pairs.AddRange(
            new CurrencyPair { Symbol = "EURUSD", BaseCurrency = "EUR", QuoteCurrency = "USD", DecimalPlaces = 5, ContractSize = 100_000m },
            new CurrencyPair { Symbol = "GBPUSD", BaseCurrency = "GBP", QuoteCurrency = "USD", DecimalPlaces = 5, ContractSize = 100_000m },
            new CurrencyPair { Symbol = "USDJPY", BaseCurrency = "USD", QuoteCurrency = "JPY", DecimalPlaces = 3, ContractSize = 100_000m },
            new CurrencyPair { Symbol = "AUDUSD", BaseCurrency = "AUD", QuoteCurrency = "USD", DecimalPlaces = 5, ContractSize = 100_000m },
            new CurrencyPair { Symbol = "USDCAD", BaseCurrency = "USD", QuoteCurrency = "CAD", DecimalPlaces = 5, ContractSize = 100_000m },
            new CurrencyPair { Symbol = "GBPJPY", BaseCurrency = "GBP", QuoteCurrency = "JPY", DecimalPlaces = 3, ContractSize = 100_000m },
            new CurrencyPair { Symbol = "EURGBP", BaseCurrency = "EUR", QuoteCurrency = "GBP", DecimalPlaces = 5, ContractSize = 100_000m },
            new CurrencyPair { Symbol = "NZDUSD", BaseCurrency = "NZD", QuoteCurrency = "USD", DecimalPlaces = 5, ContractSize = 100_000m },
            new CurrencyPair { Symbol = "USDCHF", BaseCurrency = "USD", QuoteCurrency = "CHF", DecimalPlaces = 5, ContractSize = 100_000m },
            new CurrencyPair { Symbol = "EURJPY", BaseCurrency = "EUR", QuoteCurrency = "JPY", DecimalPlaces = 3, ContractSize = 100_000m }
        );

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded {Count} currency pairs", 10);
    }

    private Task SeedTradingAccountAsync(DbContext db, CancellationToken ct)
    {
        // Trading accounts are created at runtime when the EA registers
        // via POST /auth/register. No seed data needed.
        return Task.CompletedTask;
    }

    private async Task SeedRiskProfileAsync(DbContext db, CancellationToken ct)
    {
        var profiles = db.Set<RiskProfile>();
        if (await profiles.AnyAsync(ct)) return;

        profiles.AddRange(
            new RiskProfile
            {
                Name = "Conservative (Default)",
                MaxLotSizePerTrade = 0.5m,
                MaxDailyDrawdownPct = 2m,
                MaxTotalDrawdownPct = 6m,
                MaxOpenPositions = 3,
                MaxDailyTrades = 6,
                MaxRiskPerTradePct = 1m,
                MaxSymbolExposurePct = 3m,
                IsDefault = true,
                DrawdownRecoveryThresholdPct = 1.5m,
                RecoveryLotSizeMultiplier = 0.5m,
                RecoveryExitThresholdPct = 0.5m,
            },
            new RiskProfile
            {
                Name = "Moderate",
                MaxLotSizePerTrade = 1m,
                MaxDailyDrawdownPct = 3m,
                MaxTotalDrawdownPct = 10m,
                MaxOpenPositions = 5,
                MaxDailyTrades = 10,
                MaxRiskPerTradePct = 2m,
                MaxSymbolExposurePct = 5m,
                IsDefault = false,
            }
        );

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded risk profiles");
    }

    private async Task SeedStrategiesAsync(DbContext db, CancellationToken ct)
    {
        var strategies = db.Set<Strategy>();
        if (await strategies.AnyAsync(ct)) return;

        var defaultProfile = await db.Set<RiskProfile>().FirstAsync(r => r.IsDefault, ct);

        strategies.AddRange(
            new Strategy
            {
                Name = "EURUSD MA Crossover H1",
                Description = "Moving average crossover on EUR/USD hourly chart",
                StrategyType = StrategyType.MovingAverageCrossover,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = """{"FastPeriod":9,"SlowPeriod":21,"SignalSmoothing":3}""",
                Status = StrategyStatus.Active,
                RiskProfileId = defaultProfile.Id,
                CreatedAt = DateTime.UtcNow,
            },
            new Strategy
            {
                Name = "GBPUSD RSI Reversion M15",
                Description = "RSI mean-reversion on GBP/USD 15-minute chart",
                StrategyType = StrategyType.RSIReversion,
                Symbol = "GBPUSD",
                Timeframe = Timeframe.M15,
                ParametersJson = """{"RsiPeriod":14,"OverboughtLevel":70,"OversoldLevel":30}""",
                Status = StrategyStatus.Active,
                RiskProfileId = defaultProfile.Id,
                CreatedAt = DateTime.UtcNow,
            },
            new Strategy
            {
                Name = "USDJPY Breakout Scalper M5",
                Description = "Breakout scalper on USD/JPY 5-minute chart",
                StrategyType = StrategyType.BreakoutScalper,
                Symbol = "USDJPY",
                Timeframe = Timeframe.M5,
                ParametersJson = """{"LookbackPeriod":20,"BreakoutMultiplier":1.5,"ATRPeriod":14}""",
                Status = StrategyStatus.Active,
                RiskProfileId = defaultProfile.Id,
                CreatedAt = DateTime.UtcNow,
            },
            new Strategy
            {
                Name = "EURUSD Bollinger Band Reversion H4",
                Description = "Bollinger Band mean-reversion on EUR/USD 4-hour chart",
                StrategyType = StrategyType.BollingerBandReversion,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H4,
                ParametersJson = """{"Period":20,"StdDevMultiplier":2.0,"RSIPeriod":14}""",
                Status = StrategyStatus.Paused,
                RiskProfileId = defaultProfile.Id,
                CreatedAt = DateTime.UtcNow,
            },
            new Strategy
            {
                Name = "GBPJPY Session Breakout H1",
                Description = "Session breakout on GBP/JPY at London open",
                StrategyType = StrategyType.SessionBreakout,
                Symbol = "GBPJPY",
                Timeframe = Timeframe.H1,
                ParametersJson = """{"SessionStart":8,"SessionEnd":10,"BufferPips":15}""",
                Status = StrategyStatus.Paused,
                RiskProfileId = defaultProfile.Id,
                CreatedAt = DateTime.UtcNow,
            }
        );

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded {Count} strategies", 5);
    }

    private async Task SeedLivePricesAsync(DbContext db, CancellationToken ct)
    {
        var prices = db.Set<LivePrice>();
        if (await prices.AnyAsync(ct)) return;

        var now = DateTime.UtcNow;
        prices.AddRange(
            new LivePrice { Symbol = "EURUSD", Bid = 1.08520m, Ask = 1.08535m, Timestamp = now },
            new LivePrice { Symbol = "GBPUSD", Bid = 1.27150m, Ask = 1.27170m, Timestamp = now },
            new LivePrice { Symbol = "USDJPY", Bid = 149.850m, Ask = 149.865m, Timestamp = now },
            new LivePrice { Symbol = "AUDUSD", Bid = 0.65720m, Ask = 0.65740m, Timestamp = now },
            new LivePrice { Symbol = "USDCAD", Bid = 1.35420m, Ask = 1.35440m, Timestamp = now },
            new LivePrice { Symbol = "GBPJPY", Bid = 190.520m, Ask = 190.550m, Timestamp = now },
            new LivePrice { Symbol = "EURGBP", Bid = 0.85340m, Ask = 0.85360m, Timestamp = now },
            new LivePrice { Symbol = "NZDUSD", Bid = 0.61150m, Ask = 0.61170m, Timestamp = now },
            new LivePrice { Symbol = "USDCHF", Bid = 0.87650m, Ask = 0.87670m, Timestamp = now },
            new LivePrice { Symbol = "EURJPY", Bid = 162.650m, Ask = 162.680m, Timestamp = now }
        );

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded live prices for {Count} pairs", 10);
    }

    private async Task SeedInitialMLTrainingRunsAsync(DbContext db, CancellationToken ct)
    {
        var runs = db.Set<MLTrainingRun>();
        if (await runs.AnyAsync(ct)) return;

        // Queue an initial training run for each active strategy's symbol/timeframe.
        // The MLTrainingWorker will pick these up and train the first models.
        var activeStrategies = await db.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .Select(s => new { s.Symbol, s.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var s in activeStrategies)
        {
            // Default architecture run (TrainerSelector will auto-select)
            runs.Add(new MLTrainingRun
            {
                Symbol      = s.Symbol,
                Timeframe   = s.Timeframe,
                TriggerType = TriggerType.Manual,
                Status      = RunStatus.Queued,
                FromDate    = now.AddDays(-730),
                ToDate      = now,
                StartedAt   = now,
            });

            // SMOTE run for class-imbalanced data
            runs.Add(new MLTrainingRun
            {
                Symbol              = s.Symbol,
                Timeframe           = s.Timeframe,
                TriggerType         = TriggerType.Manual,
                Status              = RunStatus.Queued,
                FromDate            = now.AddDays(-730),
                ToDate              = now,
                StartedAt           = now,
                LearnerArchitecture = LearnerArchitecture.Smote,
            });
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded {Count} initial ML training runs", activeStrategies.Count * 2);
    }

    private async Task SeedEngineConfigAsync(DbContext db, CancellationToken ct)
    {
        var configs = db.Set<EngineConfig>();
        if (await configs.AnyAsync(ct)) return;

        configs.AddRange(
            new EngineConfig
            {
                Key = "Engine:TradingMode",
                Value = "Paper",
                Description = "Trading mode: Paper or Live",
                DataType = ConfigDataType.String,
                IsHotReloadable = false,
            },
            new EngineConfig
            {
                Key = "Engine:ActiveBrokerType",
                Value = "EA",
                Description = "The broker adapter type — EA is the sole adapter",
                DataType = ConfigDataType.String,
                IsHotReloadable = false,
            },
            new EngineConfig
            {
                Key = "MarketData:IntervalSeconds",
                Value = "5",
                Description = "Interval in seconds between market data polling cycles",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true,
            },
            new EngineConfig
            {
                Key = "StrategyWorker:IntervalSeconds",
                Value = "10",
                Description = "Interval in seconds between strategy evaluation cycles",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true,
            },
            new EngineConfig
            {
                Key = "RiskMonitor:IntervalSeconds",
                Value = "15",
                Description = "Interval in seconds between risk monitoring checks",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true,
            },
            new EngineConfig
            {
                Key = "NewsFilter:HaltMinutesBefore",
                Value = "30",
                Description = "Minutes before a high-impact news event to halt trading",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true,
            },
            new EngineConfig
            {
                Key = "NewsFilter:HaltMinutesAfter",
                Value = "15",
                Description = "Minutes after a high-impact news event to resume trading",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true,
            },
            new EngineConfig
            {
                Key = "EA:HeartbeatTimeoutSeconds",
                Value = "60",
                Description = "Seconds before an EA instance is considered disconnected",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true,
            },
            new EngineConfig
            {
                Key = "MLTraining:Enabled",
                Value = "true",
                Description = "Whether ML model training workers are active",
                DataType = ConfigDataType.Bool,
                IsHotReloadable = true,
            },
            new EngineConfig
            {
                Key = "DrawdownMonitor:IntervalSeconds",
                Value = "30",
                Description = "Interval in seconds between drawdown monitoring checks",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true,
            },
            // ── ML model quality improvements ─────────────────────────────────
            new EngineConfig
            {
                Key = "MLTraining:MinF1Score",
                Value = "0.10",
                Description = "Minimum F1 score to promote — rejects single-class predictors",
                DataType = ConfigDataType.Decimal,
                IsHotReloadable = true,
            },
            new EngineConfig
            {
                Key = "MLTraining:UseClassWeights",
                Value = "true",
                Description = "Apply inverse-frequency class weighting during training",
                DataType = ConfigDataType.Bool,
                IsHotReloadable = true,
            },
            new EngineConfig
            {
                Key = "MLTraining:UseTripleBarrier",
                Value = "true",
                Description = "Use triple-barrier labeling (profit/stop/time) instead of next-bar direction",
                DataType = ConfigDataType.Bool,
                IsHotReloadable = true,
            },
            new EngineConfig
            {
                Key = "MLTraining:TrainingDataWindowDays",
                Value = "730",
                Description = "Default training data window in days for ML retraining runs",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true,
            }
        );

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded engine configuration entries");
    }
}
