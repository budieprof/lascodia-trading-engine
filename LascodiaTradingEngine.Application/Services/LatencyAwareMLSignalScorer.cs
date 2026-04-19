using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Decorator around <see cref="IMLSignalScorer"/> that tracks inference latency against
/// an SLA defined in <c>MLInference:MaxLatencyMs</c> (default 50ms).
///
/// When latency exceeds the SLA:
/// <list type="bullet">
///   <item>Logs a warning with model ID, symbol, timeframe, and elapsed time.</item>
///   <item>Increments <c>MLInference:{Symbol}:{Tf}:LatencyBreachCount</c> in EngineConfig.</item>
///   <item>When breachCount exceeds 10 in the last hour, creates an alert suggesting
///         model simplification.</item>
///   <item>Returns the result anyway — the timeout is advisory, not cancelling.</item>
/// </list>
///
/// Also maintains a rolling average latency in <c>MLInference:{Symbol}:{Tf}:AvgLatencyMs</c>.
/// </summary>
public sealed class LatencyAwareMLSignalScorer : IMLSignalScorer
{
    private readonly IMLSignalScorer _inner;
    private readonly IReadApplicationDbContext _readContext;
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly ILogger<LatencyAwareMLSignalScorer> _logger;

    /// <summary>Default latency SLA in milliseconds when not configured.</summary>
    private const int DefaultMaxLatencyMs = 50;

    /// <summary>Breach count threshold before firing a model simplification alert.</summary>
    private const int AlertBreachThreshold = 10;

    /// <summary>
    /// Exponential moving average (EMA) smoothing factor. α = 0.1 means the rolling
    /// average adapts slowly, smoothing out transient spikes while tracking sustained shifts.
    /// </summary>
    private const double EmaSmoothingFactor = 0.1;

    /// <summary>
    /// In-memory rolling averages per symbol:timeframe. Updated on every call.
    /// Static so the state survives across scoped resolutions.
    /// </summary>
    private static readonly ConcurrentDictionary<string, double> _rollingAverages = new();

    /// <summary>
    /// Tracks breach timestamps per symbol:timeframe for hourly breach-rate calculation.
    /// Static to survive across scoped instances.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _breachTimestamps = new();

    public LatencyAwareMLSignalScorer(
        IMLSignalScorer inner,
        IReadApplicationDbContext readContext,
        IWriteApplicationDbContext writeContext,
        ILogger<LatencyAwareMLSignalScorer> logger)
    {
        _inner        = inner;
        _readContext   = readContext;
        _writeContext  = writeContext;
        _logger       = logger;
    }

    public async Task<MLScoreResult> ScoreAsync(
        TradeSignal signal,
        IReadOnlyList<Candle> candles,
        CancellationToken cancellationToken)
    {
        var timeframe = candles.Count > 0 ? candles[0].Timeframe : Timeframe.H1;
        var symbol    = signal.Symbol ?? string.Empty;
        var groupKey  = $"{symbol}:{timeframe}";

        // Read configured SLA
        var maxLatencyMs = await GetConfigIntAsync(
            "MLInference:MaxLatencyMs", DefaultMaxLatencyMs, cancellationToken);

        // Score with timing
        var sw = Stopwatch.StartNew();
        var result = await _inner.ScoreAsync(signal, candles, cancellationToken);
        sw.Stop();

        var elapsedMs = sw.Elapsed.TotalMilliseconds;

        // Update rolling average (EMA)
        UpdateRollingAverage(groupKey, elapsedMs);

        // Check SLA breach
        if (elapsedMs > maxLatencyMs)
        {
            _logger.LogWarning(
                "MLInference SLA breach: model {ModelId}, {Symbol}/{Tf} took {Elapsed:F1}ms " +
                "(SLA: {Sla}ms)",
                result.MLModelId, symbol, timeframe, elapsedMs, maxLatencyMs);

            await RecordLatencyBreachAsync(groupKey, symbol, timeframe, elapsedMs, cancellationToken);
        }

        // Persist rolling average periodically (every ~10 calls via simple modulo on breach queue size)
        await PersistRollingAverageAsync(groupKey, cancellationToken);

        return result;
    }

    /// <summary>
    /// Updates the in-memory EMA for this symbol:timeframe group.
    /// </summary>
    private static void UpdateRollingAverage(string groupKey, double elapsedMs)
    {
        _rollingAverages.AddOrUpdate(
            groupKey,
            elapsedMs,
            (_, prev) => (EmaSmoothingFactor * elapsedMs) + ((1.0 - EmaSmoothingFactor) * prev));
    }

    /// <summary>
    /// Records a latency breach: increments the in-memory breach counter and checks
    /// whether the hourly breach threshold has been exceeded.
    /// </summary>
    private async Task RecordLatencyBreachAsync(
        string groupKey, string symbol, Timeframe timeframe,
        double elapsedMs, CancellationToken ct)
    {
        // Track breach timestamp
        var queue = _breachTimestamps.GetOrAdd(groupKey, _ => new ConcurrentQueue<DateTime>());
        queue.Enqueue(DateTime.UtcNow);

        // Prune entries older than 1 hour
        var cutoff = DateTime.UtcNow.AddHours(-1);
        while (queue.TryPeek(out var oldest) && oldest < cutoff)
            queue.TryDequeue(out _);

        var recentBreachCount = queue.Count;

        // Persist breach count to EngineConfig
        try
        {
            var breachKey = $"MLInference:{symbol}:{timeframe}:LatencyBreachCount";
            await UpsertConfigAsync(breachKey, recentBreachCount.ToString(),
                $"Latency SLA breaches in the last hour for {symbol}/{timeframe}",
                ConfigDataType.Int, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist latency breach count for {GroupKey}", groupKey);
        }

        // Fire alert when threshold exceeded
        if (recentBreachCount > AlertBreachThreshold)
        {
            await CreateLatencyAlertAsync(symbol, timeframe, recentBreachCount, elapsedMs, ct);
        }
    }

    /// <summary>
    /// Creates an alert suggesting model simplification when breach count exceeds the threshold.
    /// </summary>
    private async Task CreateLatencyAlertAsync(
        string symbol, Timeframe timeframe,
        int breachCount, double latestLatencyMs,
        CancellationToken ct)
    {
        try
        {
            var writeCtx = _writeContext.GetDbContext();

            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = symbol,
                ConditionJson = JsonSerializer.Serialize(new
                {
                    Reason          = "inference_latency_sla",
                    Symbol          = symbol,
                    Timeframe       = timeframe.ToString(),
                    BreachCount     = breachCount,
                    LatestLatencyMs = Math.Round(latestLatencyMs, 1),
                    Message         = $"ML inference for {symbol}/{timeframe} has breached the " +
                                     $"latency SLA {breachCount} times in the last hour " +
                                     $"(latest: {latestLatencyMs:F1}ms). Consider simplifying " +
                                     $"the model architecture or reducing ensemble size."
                }),
                IsActive = true,
                Severity = AlertSeverity.High
            });

            await _writeContext.SaveChangesAsync(ct);

            _logger.LogWarning(
                "MLInference: created latency alert for {Symbol}/{Tf} — {BreachCount} breaches in 1h",
                symbol, timeframe, breachCount);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create latency alert for {Symbol}/{Tf}", symbol, timeframe);
        }
    }

    /// <summary>
    /// Persists the current rolling average to EngineConfig.
    /// </summary>
    private async Task PersistRollingAverageAsync(string groupKey, CancellationToken ct)
    {
        if (!_rollingAverages.TryGetValue(groupKey, out var avg))
            return;

        try
        {
            var parts = groupKey.Split(':');
            if (parts.Length < 2) return;

            var configKey = $"MLInference:{parts[0]}:{parts[1]}:AvgLatencyMs";
            await UpsertConfigAsync(configKey, Math.Round(avg, 2).ToString(),
                $"Rolling average inference latency (ms) for {parts[0]}/{parts[1]}",
                ConfigDataType.Decimal, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist rolling average for {GroupKey}", groupKey);
        }
    }

    /// <summary>
    /// Reads an integer EngineConfig value, returning the default if not found or unparseable.
    /// </summary>
    private async Task<int> GetConfigIntAsync(string key, int defaultValue, CancellationToken ct)
    {
        try
        {
            var config = await _readContext.GetDbContext()
                .Set<EngineConfig>()
                .Where(c => c.Key == key && !c.IsDeleted)
                .Select(c => c.Value)
                .FirstOrDefaultAsync(ct);

            return config is not null && int.TryParse(config, out var val) ? val : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Upserts an EngineConfig entry — updates if it exists, inserts if not.
    /// </summary>
    private Task UpsertConfigAsync(
        string key, string value, string description,
        ConfigDataType dataType, CancellationToken ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(
            _writeContext.GetDbContext(), key, value, dataType, description, ct: ct);
}
