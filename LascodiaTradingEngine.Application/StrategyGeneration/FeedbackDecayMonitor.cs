using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Tracks the accuracy of performance feedback predictions over time and
/// adaptively adjusts the recency decay half-life used by the strategy
/// generation pipeline. Persists its state in the EngineConfig table so it
/// survives restarts.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IFeedbackDecayMonitor))]
internal sealed class FeedbackDecayMonitor : IFeedbackDecayMonitor
{
    // ── Constants ────────────────────────────────────────────────────────
    internal const string ConfigKey = "StrategyGeneration:FeedbackDecayState";
    internal const double DefaultHalfLifeDays = 62.0;
    internal const double MinHalfLifeDays = 30.0;
    internal const double MaxHalfLifeDays = 120.0;
    internal const int MinSamplesForAdjustment = 5;
    internal const int MaxPredictionSnapshots = 10;
    internal const int MaxAccuracyWindows = 30;

    // ── Inner records (JSON persistence) ────────────────────────────────
    internal sealed record DecayState
    {
        public int Version { get; init; } = 1;
        public double EffectiveHalfLifeDays { get; init; } = DefaultHalfLifeDays;
        public List<PredictionSnapshot> RecentPredictions { get; init; } = [];
        public List<AccuracyWindow> AccuracyHistory { get; init; } = [];
    }

    internal sealed record PredictionSnapshot(DateTime CycleDateUtc, Dictionary<string, double> Predictions);
    internal sealed record AccuracyWindow(DateTime CycleDateUtc, double MeanSquaredError, int SampleCount);

    // ── Fields ───────────────────────────────────────────────────────────
    private readonly ILogger<FeedbackDecayMonitor> _logger;
    private readonly Lock _lock = new();
    private double _effectiveHalfLifeDays = DefaultHalfLifeDays;
    private DecayState? _state;
    private bool _stateLoaded;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Constructor ──────────────────────────────────────────────────────
    public FeedbackDecayMonitor(ILogger<FeedbackDecayMonitor> logger)
    {
        _logger = logger;
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public double GetEffectiveHalfLifeDays()
    {
        lock (_lock)
        {
            return _effectiveHalfLifeDays;
        }
    }

    /// <inheritdoc />
    public async Task RecordPredictionsAsync(
        DbContext readDb,
        DbContext writeDb,
        Dictionary<(StrategyType, MarketRegimeEnum), double> predictions,
        CancellationToken ct)
    {
        try
        {
            await EnsureStateLoadedAsync(readDb, ct);

            // Convert tuple-keyed dict to string-keyed for JSON serialization
            var stringKeyed = new Dictionary<string, double>(predictions.Count);
            foreach (var ((strategyType, regime), value) in predictions)
            {
                stringKeyed[$"{strategyType}:{regime}"] = value;
            }

            var snapshot = new PredictionSnapshot(DateTime.UtcNow, stringKeyed);

            DecayState state;
            lock (_lock)
            {
                state = _state ?? new DecayState();
                var updated = new List<PredictionSnapshot>(state.RecentPredictions) { snapshot };

                // Trim to max snapshots (keep most recent)
                if (updated.Count > MaxPredictionSnapshots)
                    updated.RemoveRange(0, updated.Count - MaxPredictionSnapshots);

                state = state with { RecentPredictions = updated };
                _state = state;
            }

            await PersistStateAsync(writeDb, state, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FeedbackDecayMonitor: failed to record predictions — continuing with current state");
        }
    }

    /// <inheritdoc />
    public async Task EvaluateAndAdjustAsync(DbContext readDb, DbContext writeDb, CancellationToken ct)
    {
        try
        {
            await EnsureStateLoadedAsync(readDb, ct);

            DecayState state;
            lock (_lock)
            {
                state = _state ?? new DecayState();
            }

            // Need at least one prediction snapshot to evaluate
            if (state.RecentPredictions.Count == 0)
            {
                _logger.LogDebug("FeedbackDecayMonitor: no prediction snapshots to evaluate");
                return;
            }

            // Use the most recent snapshot
            var snapshot = state.RecentPredictions[^1];

            // Query strategies created since that snapshot that have reached a terminal state
            var outcomes = await readDb.Set<Strategy>()
                .Where(s => s.Name.StartsWith("Auto-") && s.CreatedAt >= snapshot.CycleDateUtc)
                .Select(s => new
                {
                    s.StrategyType,
                    s.ScreeningMetricsJson,
                    s.LifecycleStage,
                    s.IsDeleted
                })
                .ToListAsync(ct);

            if (outcomes.Count == 0)
            {
                _logger.LogDebug("FeedbackDecayMonitor: no outcome data available yet for snapshot from {Date}", snapshot.CycleDateUtc);
                return;
            }

            // Compute actual survival rates and Brier MSE
            double sumSquaredError = 0;
            int sampleCount = 0;

            foreach (var (key, predicted) in snapshot.Predictions)
            {
                // Parse the key back to StrategyType and MarketRegime
                var parts = key.Split(':');
                if (parts.Length != 2)
                    continue;

                if (!Enum.TryParse<StrategyType>(parts[0], out var strategyType))
                    continue;

                // Find matching outcomes for this strategy type
                var matching = outcomes.Where(o => o.StrategyType == strategyType).ToList();
                if (matching.Count == 0)
                    continue;

                // Survived = not deleted AND lifecycle stage >= BacktestQualified
                double survived = matching.Count(o =>
                    !o.IsDeleted && o.LifecycleStage >= StrategyLifecycleStage.BacktestQualified);
                double actual = survived / matching.Count;

                double error = predicted - actual;
                sumSquaredError += error * error;
                sampleCount++;
            }

            if (sampleCount < MinSamplesForAdjustment)
            {
                _logger.LogDebug(
                    "FeedbackDecayMonitor: only {Samples} samples available, need {Min} for adjustment",
                    sampleCount, MinSamplesForAdjustment);
                return;
            }

            double mse = sumSquaredError / sampleCount;

            // Determine adjustment
            double adjustment = 0;
            if (mse > 0.15)
            {
                // Predictions inaccurate — shorten half-life to trust recent data more
                adjustment = -5.0;
            }
            else if (mse < 0.05)
            {
                // Predictions accurate — lengthen half-life to leverage more history
                adjustment = 2.0;
            }

            var accuracyEntry = new AccuracyWindow(DateTime.UtcNow, mse, sampleCount);

            lock (_lock)
            {
                if (adjustment != 0)
                {
                    _effectiveHalfLifeDays = Math.Clamp(
                        _effectiveHalfLifeDays + adjustment,
                        MinHalfLifeDays,
                        MaxHalfLifeDays);
                }

                var updatedHistory = new List<AccuracyWindow>(state.AccuracyHistory) { accuracyEntry };

                // Trim to max windows (keep most recent)
                if (updatedHistory.Count > MaxAccuracyWindows)
                    updatedHistory.RemoveRange(0, updatedHistory.Count - MaxAccuracyWindows);

                state = state with
                {
                    EffectiveHalfLifeDays = _effectiveHalfLifeDays,
                    AccuracyHistory = updatedHistory
                };
                _state = state;
            }

            await PersistStateAsync(writeDb, state, ct);

            _logger.LogInformation(
                "FeedbackDecayMonitor: MSE={Mse:F4} samples={Samples} adjustment={Adj:+0.0;-0.0;0} halfLife={HalfLife:F1}d",
                mse, sampleCount, adjustment, _effectiveHalfLifeDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FeedbackDecayMonitor: evaluation failed — continuing with current half-life");
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private async Task EnsureStateLoadedAsync(DbContext readDb, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_stateLoaded)
                return;
        }

        try
        {
            var config = await readDb.Set<EngineConfig>()
                .FirstOrDefaultAsync(c => c.Key == ConfigKey, ct);

            DecayState? loaded = null;
            if (config is not null && !string.IsNullOrWhiteSpace(config.Value))
            {
                try
                {
                    loaded = JsonSerializer.Deserialize<DecayState>(config.Value, JsonOpts);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "FeedbackDecayMonitor: corrupt state in EngineConfig — resetting to defaults");
                }
            }

            lock (_lock)
            {
                _state = loaded ?? new DecayState();
                _effectiveHalfLifeDays = _state.EffectiveHalfLifeDays;
                _stateLoaded = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FeedbackDecayMonitor: failed to load state from EngineConfig — using defaults");

            lock (_lock)
            {
                _state ??= new DecayState();
                _stateLoaded = true;
            }
        }
    }

    private async Task PersistStateAsync(DbContext writeDb, DecayState state, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOpts);

            var existing = await writeDb.Set<EngineConfig>()
                .FirstOrDefaultAsync(c => c.Key == ConfigKey, ct);

            if (existing is not null)
            {
                existing.Value = json;
                existing.LastUpdatedAt = DateTime.UtcNow;
            }
            else
            {
                writeDb.Set<EngineConfig>().Add(new EngineConfig
                {
                    Key = ConfigKey,
                    Value = json,
                    Description = "Feedback decay monitor state — tracks prediction accuracy and adaptive half-life",
                    DataType = ConfigDataType.Json,
                    IsHotReloadable = false,
                    LastUpdatedAt = DateTime.UtcNow
                });
            }

            await writeDb.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FeedbackDecayMonitor: failed to persist state to EngineConfig — changes will be retried next cycle");
        }
    }
}
