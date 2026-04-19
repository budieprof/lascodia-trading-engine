using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Backtesting;

public sealed record BacktestWorkerSettings(
    bool Enabled,
    int SchedulePollSeconds,
    int CooldownDays,
    int WindowDays,
    decimal InitialBalance,
    int MaxQueuedPerCycle,
    int MinCandlesRequired,
    int StaleRunMinutes,
    int MaxRetryAttempts,
    int RetryBackoffSeconds,
    int CandleGapMultiplier,
    int MaxCandlesPerRun,
    decimal AutoWalkForwardInSampleRatio,
    decimal AutoWalkForwardOutOfSampleRatio,
    int AutoWalkForwardMinInSampleDays,
    int AutoWalkForwardMinOutOfSampleDays);

public sealed record WalkForwardWorkerSettings(
    int StaleRunMinutes,
    int MaxRetryAttempts,
    int RetryBackoffSeconds,
    int CandleGapMultiplier,
    int MaxCandlesPerRun,
    int MaxParallelRuns,
    decimal MaxCoefficientOfVariation,
    int MinInSampleDays,
    int MinOutOfSampleDays,
    int MinCandlesPerFold,
    int MinTradesPerFold);

public interface IValidationSettingsProvider
{
    Task<BacktestWorkerSettings> GetBacktestSettingsAsync(
        DbContext ctx,
        ILogger logger,
        CancellationToken ct);

    Task<WalkForwardWorkerSettings> GetWalkForwardSettingsAsync(
        DbContext ctx,
        ILogger logger,
        CancellationToken ct);

    Task<int> GetIntAsync(
        DbContext ctx,
        ILogger logger,
        string key,
        int defaultValue,
        CancellationToken ct,
        int? minInclusive = null,
        int? maxInclusive = null);

    Task<decimal> GetDecimalAsync(
        DbContext ctx,
        ILogger logger,
        string key,
        decimal defaultValue,
        CancellationToken ct,
        decimal? minInclusive = null,
        decimal? maxInclusive = null);

    Task<bool> GetBoolAsync(
        DbContext ctx,
        ILogger logger,
        string key,
        bool defaultValue,
        CancellationToken ct);
}

internal sealed class ValidationSettingsProvider : IValidationSettingsProvider
{
    public async Task<BacktestWorkerSettings> GetBacktestSettingsAsync(
        DbContext ctx,
        ILogger logger,
        CancellationToken ct)
    {
        return new BacktestWorkerSettings(
            Enabled: await GetBoolAsync(ctx, logger, "Backtest:Enabled", true, ct),
            SchedulePollSeconds: await GetIntAsync(ctx, logger, "Backtest:SchedulePollSeconds", 3600, ct, minInclusive: 10),
            CooldownDays: await GetIntAsync(ctx, logger, "Backtest:CooldownDays", 7, ct, minInclusive: 0),
            WindowDays: await GetIntAsync(ctx, logger, "Backtest:WindowDays", 365, ct, minInclusive: 1),
            InitialBalance: await GetDecimalAsync(ctx, logger, "Backtest:InitialBalance", 10_000m, ct, minInclusive: 0.01m),
            MaxQueuedPerCycle: await GetIntAsync(ctx, logger, "Backtest:MaxQueuedPerCycle", 5, ct, minInclusive: 1),
            MinCandlesRequired: await GetIntAsync(ctx, logger, "Backtest:MinCandlesRequired", 100, ct, minInclusive: 1),
            StaleRunMinutes: await GetIntAsync(ctx, logger, "Backtest:StaleRunMinutes", 120, ct, minInclusive: 5),
            MaxRetryAttempts: await GetIntAsync(ctx, logger, "Backtest:RetryMaxAttempts", 2, ct, minInclusive: 0, maxInclusive: 10),
            RetryBackoffSeconds: await GetIntAsync(ctx, logger, "Backtest:RetryBackoffSeconds", 30, ct, minInclusive: 5),
            CandleGapMultiplier: await GetIntAsync(ctx, logger, "Backtest:MaxGapMultiplier", 5, ct, minInclusive: 1),
            MaxCandlesPerRun: await GetIntAsync(ctx, logger, "Backtest:MaxCandlesPerRun", 100_000, ct, minInclusive: 100),
            AutoWalkForwardInSampleRatio: await GetDecimalAsync(ctx, logger, "Backtest:AutoWalkForwardInSampleRatio", 0.70m, ct, minInclusive: 0.10m, maxInclusive: 0.95m),
            AutoWalkForwardOutOfSampleRatio: await GetDecimalAsync(ctx, logger, "Backtest:AutoWalkForwardOutOfSampleRatio", 0.30m, ct, minInclusive: 0.05m, maxInclusive: 0.90m),
            AutoWalkForwardMinInSampleDays: await GetIntAsync(ctx, logger, "Backtest:AutoWalkForwardMinInSampleDays", 14, ct, minInclusive: 1),
            AutoWalkForwardMinOutOfSampleDays: await GetIntAsync(ctx, logger, "Backtest:AutoWalkForwardMinOutOfSampleDays", 7, ct, minInclusive: 1));
    }

    public async Task<WalkForwardWorkerSettings> GetWalkForwardSettingsAsync(
        DbContext ctx,
        ILogger logger,
        CancellationToken ct)
    {
        return new WalkForwardWorkerSettings(
            StaleRunMinutes: await GetIntAsync(ctx, logger, "WalkForward:StaleRunMinutes", 120, ct, minInclusive: 5),
            MaxRetryAttempts: await GetIntAsync(ctx, logger, "WalkForward:RetryMaxAttempts", 2, ct, minInclusive: 0, maxInclusive: 10),
            RetryBackoffSeconds: await GetIntAsync(ctx, logger, "WalkForward:RetryBackoffSeconds", 30, ct, minInclusive: 5),
            CandleGapMultiplier: await GetIntAsync(ctx, logger, "WalkForward:MaxGapMultiplier", 5, ct, minInclusive: 1),
            MaxCandlesPerRun: await GetIntAsync(ctx, logger, "WalkForward:MaxCandlesPerRun", 150_000, ct, minInclusive: 100),
            MaxParallelRuns: await GetIntAsync(ctx, logger, "WalkForward:MaxParallelRuns", 4, ct, minInclusive: 1, maxInclusive: 32),
            // Canonical config key shared with OptimizationConfigProvider — keep in sync.
            MaxCoefficientOfVariation: await GetDecimalAsync(ctx, logger, "Optimization:MaxCvCoefficientOfVariation", 0.50m, ct, minInclusive: 0m),
            // Per-fold minimums. Defaults sized for H1 data (≥14 days ≈ 336 candles per IS fold
            // before weekends, ≥7 days OOS). Tiny folds (e.g. 5-day IS / 2-day OOS) produce
            // unreliable stddev on the OOS CV gate even when the 3-window minimum is met, so
            // we reject below a hard minimum floor.
            MinInSampleDays: await GetIntAsync(ctx, logger, "WalkForward:MinInSampleDays", 14, ct, minInclusive: 1),
            MinOutOfSampleDays: await GetIntAsync(ctx, logger, "WalkForward:MinOutOfSampleDays", 7, ct, minInclusive: 1),
            // Per-fold candle and trade floors. Fewer than 60 candles per fold cannot support
            // 14-period indicators (ATR/ADX) with meaningful warmup. Fewer than 5 trades per
            // fold produces a Sharpe estimate with >100% standard error.
            MinCandlesPerFold: await GetIntAsync(ctx, logger, "WalkForward:MinCandlesPerFold", 60, ct, minInclusive: 1),
            MinTradesPerFold: await GetIntAsync(ctx, logger, "WalkForward:MinTradesPerFold", 5, ct, minInclusive: 0));
    }

    public async Task<int> GetIntAsync(
        DbContext ctx,
        ILogger logger,
        string key,
        int defaultValue,
        CancellationToken ct,
        int? minInclusive = null,
        int? maxInclusive = null)
    {
        var rawValue = await GetConfigValueAsync(ctx, logger, key, ct);
        if (string.IsNullOrWhiteSpace(rawValue))
            return defaultValue;

        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return LogInvalidConfigAndReturnDefault(logger, key, rawValue, defaultValue, "not a valid integer");

        if (minInclusive.HasValue && value < minInclusive.Value)
            return LogInvalidConfigAndReturnDefault(logger, key, rawValue, defaultValue, $"must be >= {minInclusive.Value}");

        if (maxInclusive.HasValue && value > maxInclusive.Value)
            return LogInvalidConfigAndReturnDefault(logger, key, rawValue, defaultValue, $"must be <= {maxInclusive.Value}");

        return value;
    }

    public async Task<decimal> GetDecimalAsync(
        DbContext ctx,
        ILogger logger,
        string key,
        decimal defaultValue,
        CancellationToken ct,
        decimal? minInclusive = null,
        decimal? maxInclusive = null)
    {
        var rawValue = await GetConfigValueAsync(ctx, logger, key, ct);
        if (string.IsNullOrWhiteSpace(rawValue))
            return defaultValue;

        if (!decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
            return LogInvalidConfigAndReturnDefault(logger, key, rawValue, defaultValue, "not a valid decimal");

        if (minInclusive.HasValue && value < minInclusive.Value)
            return LogInvalidConfigAndReturnDefault(logger, key, rawValue, defaultValue, $"must be >= {minInclusive.Value.ToString(CultureInfo.InvariantCulture)}");

        if (maxInclusive.HasValue && value > maxInclusive.Value)
            return LogInvalidConfigAndReturnDefault(logger, key, rawValue, defaultValue, $"must be <= {maxInclusive.Value.ToString(CultureInfo.InvariantCulture)}");

        return value;
    }

    public async Task<bool> GetBoolAsync(
        DbContext ctx,
        ILogger logger,
        string key,
        bool defaultValue,
        CancellationToken ct)
    {
        var rawValue = await GetConfigValueAsync(ctx, logger, key, ct);
        if (string.IsNullOrWhiteSpace(rawValue))
            return defaultValue;

        if (bool.TryParse(rawValue, out bool parsed))
            return parsed;

        return LogInvalidConfigAndReturnDefault(logger, key, rawValue, defaultValue, "not a valid boolean");
    }

    private static async Task<string?> GetConfigValueAsync(
        DbContext ctx,
        ILogger logger,
        string key,
        CancellationToken ct)
    {
        DbSet<EngineConfig>? configSet;
        try
        {
            configSet = ctx.Set<EngineConfig>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            logger.LogDebug(ex, "ValidationConfigReader: EngineConfig DbSet unavailable while loading {Key}", key);
            return null;
        }

        if (configSet == null)
            return null;

        return await configSet
            .AsNoTracking()
            .Where(config => config.Key == key)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(ct);
    }

    private static T LogInvalidConfigAndReturnDefault<T>(
        ILogger logger,
        string key,
        string rawValue,
        T defaultValue,
        string reason)
    {
        logger.LogWarning(
            "ValidationConfigReader: invalid EngineConfig {Key}={Value} ({Reason}); using default {Default}",
            key,
            rawValue,
            reason,
            defaultValue);
        return defaultValue;
    }
}
