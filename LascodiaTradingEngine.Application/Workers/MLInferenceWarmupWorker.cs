using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// On startup, pre-loads active ML models and runs a model-specific inference pass
/// with actual candle data to warm snapshot deserialization, feature engineering,
/// inference engines, and calibration before live trading needs them.
/// </summary>
public sealed class MLInferenceWarmupWorker : BackgroundService
{
    private const int RequiredCandles = MLFeatureHelper.LookbackWindow + 2;
    private const string WorkerName = nameof(MLInferenceWarmupWorker);
    private const string DistributedLockKey = "ml:inference-warmup:startup";
    private const string ConfigPrefixUpper = "MLWARMUP:";

    private const string CK_Enabled = "MLWarmup:InferenceWarmupEnabled";
    private const string CK_StartupDelaySeconds = "MLWarmup:StartupDelaySeconds";
    private const string CK_ModelTimeoutSeconds = "MLWarmup:ModelTimeoutSeconds";
    private const string CK_MaxModelsPerStartup = "MLWarmup:MaxModelsPerStartup";
    private const string CK_MaxTimeoutsBeforeAbort = "MLWarmup:MaxTimeoutsBeforeAbort";
    private const string CK_LockTimeoutSeconds = "MLWarmup:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLWarmup:DbCommandTimeoutSeconds";

    private readonly ILogger<MLInferenceWarmupWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLInferenceWarmupOptions _options;
    private int _missingDistributedLockWarningEmitted;

    public MLInferenceWarmupWorker(
        ILogger<MLInferenceWarmupWorker> logger,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLInferenceWarmupOptions? options = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MLInferenceWarmupOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Runs a one-shot startup pass through active ML inference paths.",
            TimeSpan.FromSeconds(NormalizeStartupDelaySeconds(_options.StartupDelaySeconds)));

        try
        {
            var fallbackSettings = WarmupSettings.FromSources(_configuration, _options);
            var startupDelay = WorkerStartupSequencer.GetDelay(WorkerName)
                + TimeSpan.FromSeconds(fallbackSettings.StartupDelaySeconds);

            if (startupDelay > TimeSpan.Zero)
                await Task.Delay(startupDelay, _timeProvider, stoppingToken);

            _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
            var stats = await WarmupAsync(stoppingToken);
            _healthMonitor?.RecordCycleSuccess(WorkerName, stats.ElapsedMs);
            _healthMonitor?.RecordWorkerCompleted(WorkerName);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker}: startup warm-up cancelled", WorkerName);
        }
        catch (Exception ex)
        {
            _metrics?.WorkerErrors.Add(1, Tag("worker", WorkerName));
            _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
            _healthMonitor?.RecordWorkerStopped(WorkerName, ex.Message);
            _logger.LogError(ex, "{Worker}: warm-up aborted due to critical error", WorkerName);
        }
    }

    internal async Task<WarmupStats> WarmupAsync(CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();
        var stats = new WarmupStats();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var readDb = readCtx.GetDbContext();
        var settings = await LoadSettingsAsync(readDb, stoppingToken);
        ApplyCommandTimeout(readDb, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
        {
            RecordCycleSkipped("disabled");
            _logger.LogInformation("{Worker}: disabled by {ConfigKey}", WorkerName, CK_Enabled);
            stats.ElapsedMs = ElapsedMs(sw);
            RecordSummaryMetrics(stats);
            return stats;
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is not null)
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
                stoppingToken);

            if (cycleLock is null)
            {
                _metrics?.MLInferenceWarmupLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                stats.ElapsedMs = ElapsedMs(sw);
                RecordSummaryMetrics(stats);
                _logger.LogInformation("{Worker}: skipped because another instance holds the startup warm-up lock.", WorkerName);
                return stats;
            }

            _metrics?.MLInferenceWarmupLockAttempts.Add(1, Tag("outcome", "acquired"));
        }
        else
        {
            _metrics?.MLInferenceWarmupLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; multiple instances may duplicate startup warm-up.",
                    WorkerName);
            }
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(stoppingToken);
            try
            {
                var warmupScorer = scope.ServiceProvider.GetRequiredService<IMLModelWarmupScorer>();
                await WarmupCoreAsync(readDb, warmupScorer, settings, stats, stoppingToken);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }

        stats.ElapsedMs = ElapsedMs(sw);
        RecordSummaryMetrics(stats);

        _logger.LogInformation(
            "{Worker}: completed in {Elapsed}ms - warmed={Warmed}, failed={Failed}, timedOut={TimedOut}, skippedNoCandles={SkippedNoCandles}, skippedEmptySnapshot={SkippedEmptySnapshot}, skippedInvalidModel={SkippedInvalidModel}, skippedLimit={SkippedLimit}",
            WorkerName,
            stats.ElapsedMs,
            stats.Warmed,
            stats.Failed,
            stats.TimedOut,
            stats.SkippedNoCandles,
            stats.SkippedEmptySnapshot,
            stats.SkippedInvalidModel,
            stats.SkippedLimit);

        if (stats.Failed + stats.TimedOut > 0)
        {
            _logger.LogWarning(
                "{Worker}: {ProblemCount} models did not complete warm-up - check model snapshots, feature schema, and inference engine logs",
                WorkerName,
                stats.Failed + stats.TimedOut);
        }

        return stats;
    }

    private async Task WarmupCoreAsync(
        DbContext readDb,
        IMLModelWarmupScorer warmupScorer,
        WarmupSettings settings,
        WarmupStats stats,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "{Worker}: warming active ML model core inference paths (maxModels={MaxModels}, modelTimeout={Timeout}s)",
            WorkerName,
            settings.MaxModelsPerStartup,
            settings.ModelTimeout.TotalSeconds);

        var selection = await LoadWarmupCandidatesAsync(
            readDb,
            settings.MaxModelsPerStartup,
            stoppingToken);

        stats.TotalEligibleModels = selection.TotalEligible;
        stats.SkippedEmptySnapshot = selection.SkippedEmptySnapshot;
        stats.SkippedInvalidModel = selection.SkippedInvalidModel;
        stats.SkippedLimit = selection.SkippedLimit;

        _healthMonitor?.RecordBacklogDepth(WorkerName, selection.Candidates.Count);

        if (selection.Candidates.Count == 0)
        {
            RecordCycleSkipped("no_candidates");
            _logger.LogInformation(
                "{Worker}: no snapshot-backed active ML models were eligible for startup warm-up.",
                WorkerName);
            return;
        }

        _logger.LogInformation(
            "{Worker}: found {Count} warm-up candidates with snapshots across {GroupCount} symbol/timeframe groups (eligible={Eligible}, skippedLimit={SkippedLimit}, skippedNoSnapshot={SkippedNoSnapshot}, skippedInvalid={SkippedInvalid})",
            WorkerName,
            selection.Candidates.Count,
            selection.Candidates.Select(m => (m.Symbol, m.Timeframe)).Distinct().Count(),
            selection.TotalEligible,
            selection.SkippedLimit,
            selection.SkippedEmptySnapshot,
            selection.SkippedInvalidModel);

        bool abortWarmup = false;
        foreach (var group in selection.Candidates.GroupBy(m => (m.Symbol, m.Timeframe)))
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (abortWarmup) break;

            var (symbol, timeframe) = group.Key;
            var groupCandidates = group
                .OrderBy(m => m.RegimeScope ?? string.Empty, StringComparer.Ordinal)
                .ThenByDescending(m => m.ActivatedAt ?? DateTime.MinValue)
                .ThenBy(m => m.Id)
                .ToList();
            var candles = await LoadWarmupCandlesAsync(readDb, symbol, timeframe, stoppingToken);

            if (candles.Count < RequiredCandles)
            {
                stats.SkippedNoCandles += groupCandidates.Count;
                _logger.LogWarning(
                    "{Worker}: skipping {Symbol}/{Timeframe} models - only {Count}/{Required} closed candles available",
                    WorkerName,
                    symbol,
                    timeframe,
                    candles.Count,
                    RequiredCandles);
                continue;
            }

            var currentRegime = await GetCurrentRegimeAsync(readDb, symbol, timeframe, stoppingToken);

            foreach (var candidate in groupCandidates)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (abortWarmup) break;

                var modelSw = Stopwatch.StartNew();
                using var modelCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                modelCts.CancelAfter(settings.ModelTimeout);

                try
                {
                    var model = await LoadWarmupModelAsync(readDb, candidate.Id, modelCts.Token);
                    if (model?.ModelBytes is not { Length: > 0 })
                    {
                        stats.SkippedEmptySnapshot++;
                        _logger.LogWarning(
                            "{Worker}: skipping model {ModelId} ({Symbol}/{Timeframe}) because its snapshot disappeared or is empty",
                            WorkerName,
                            candidate.Id,
                            candidate.Symbol,
                            candidate.Timeframe);
                        continue;
                    }

                    model.Symbol = NormalizeSymbol(model.Symbol);
                    var regimeForWarmup = model.RegimeScope ?? currentRegime;
                    await warmupScorer.WarmupModelAsync(
                        model,
                        candles,
                        regimeForWarmup,
                        modelCts.Token);

                    stats.Warmed++;
                    _metrics?.MLInferenceWarmupModelDurationMs.Record(
                        modelSw.Elapsed.TotalMilliseconds,
                        Tag("symbol", model.Symbol),
                        Tag("timeframe", model.Timeframe.ToString()));
                    _logger.LogDebug(
                        "{Worker}: warmed model {ModelId} ({Symbol}/{Timeframe}, regime={Regime}) in {Elapsed}ms",
                        WorkerName,
                        model.Id,
                        model.Symbol,
                        model.Timeframe,
                        regimeForWarmup ?? "global",
                        modelSw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (modelCts.IsCancellationRequested)
                {
                    stats.TimedOut++;
                    _logger.LogWarning(
                        "{Worker}: timed out warming model {ModelId} ({Symbol}/{Timeframe}) after {Timeout}s",
                        WorkerName,
                        candidate.Id,
                        candidate.Symbol,
                        candidate.Timeframe,
                        settings.ModelTimeout.TotalSeconds);

                    if (settings.MaxTimeoutsBeforeAbort > 0 &&
                        stats.TimedOut >= settings.MaxTimeoutsBeforeAbort)
                    {
                        abortWarmup = true;
                        _logger.LogWarning(
                            "{Worker}: aborting remaining startup warm-up after {TimedOut} timeout(s) to avoid background inference pile-up",
                            WorkerName,
                            stats.TimedOut);
                    }
                }
                catch (Exception ex)
                {
                    stats.Failed++;
                    _logger.LogWarning(ex,
                        "{Worker}: warm-up failed for model {ModelId} ({Symbol}/{Timeframe}). Model may have a stale snapshot, invalid weights, or incompatible feature schema.",
                        WorkerName,
                        candidate.Id,
                        candidate.Symbol,
                        candidate.Timeframe);
                }
            }
        }
    }

    private async Task<WarmupSettings> LoadSettingsAsync(
        DbContext readDb,
        CancellationToken ct)
    {
        var fallback = WarmupSettings.FromSources(_configuration, _options);

        Dictionary<string, string?> values;
        try
        {
            var rows = await readDb
                .Set<EngineConfig>()
                .AsNoTracking()
                .Where(c => c.Key.ToUpper().StartsWith(ConfigPrefixUpper) && !c.IsDeleted)
                .Select(c => new { c.Id, c.Key, Value = (string?)c.Value, c.LastUpdatedAt })
                .ToListAsync(ct);

            values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.OrderBy(r => r.LastUpdatedAt).ThenBy(r => r.Id))
            {
                if (!string.IsNullOrWhiteSpace(row.Key))
                    values[row.Key.Trim()] = row.Value;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "{Worker}: failed to read EngineConfig warm-up settings; using application configuration/defaults",
                WorkerName);
            values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        return new WarmupSettings(
            Enabled: GetBool(values, CK_Enabled, fallback.Enabled),
            StartupDelaySeconds: fallback.StartupDelaySeconds,
            ModelTimeout: TimeSpan.FromSeconds(GetInt(
                values, CK_ModelTimeoutSeconds, (int)fallback.ModelTimeout.TotalSeconds, 1, 600)),
            MaxModelsPerStartup: GetInt(
                values, CK_MaxModelsPerStartup, fallback.MaxModelsPerStartup, 1, 250_000),
            MaxTimeoutsBeforeAbort: GetInt(
                values, CK_MaxTimeoutsBeforeAbort, fallback.MaxTimeoutsBeforeAbort, 0, 100),
            LockTimeoutSeconds: GetInt(
                values, CK_LockTimeoutSeconds, fallback.LockTimeoutSeconds, 0, 300),
            DbCommandTimeoutSeconds: GetInt(
                values, CK_DbCommandTimeoutSeconds, fallback.DbCommandTimeoutSeconds, 1, 600));
    }

    private static async Task<WarmupCandidateSelection> LoadWarmupCandidatesAsync(
        DbContext readDb,
        int maxModels,
        CancellationToken ct)
    {
        var rows = await readDb
            .Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                     && !m.IsDeleted
                     && !m.IsSuppressed
                     && !m.IsMetaLearner
                     && !m.IsMamlInitializer
                     && (m.Status == MLModelStatus.Active || m.IsFallbackChampion))
            .Select(m => new
            {
                m.Id,
                m.Symbol,
                m.Timeframe,
                m.RegimeScope,
                m.ActivatedAt,
                HasSnapshot = m.ModelBytes != null && m.ModelBytes.Length > 0
            })
            .ToListAsync(ct);

        var validRows = rows
            .Select(m => new WarmupModelCandidate(
                m.Id,
                NormalizeSymbol(m.Symbol),
                m.Timeframe,
                m.RegimeScope,
                m.ActivatedAt,
                m.HasSnapshot))
            .ToList();

        int invalidModelCount = validRows.Count(m => string.IsNullOrWhiteSpace(m.Symbol));
        validRows = validRows
            .Where(m => !string.IsNullOrWhiteSpace(m.Symbol))
            .ToList();

        int skippedEmptySnapshot = validRows.Count(m => !m.HasSnapshot);
        var snapshotBacked = validRows
            .Where(m => m.HasSnapshot)
            .OrderByDescending(m => m.ActivatedAt ?? DateTime.MinValue)
            .ThenBy(m => m.Symbol, StringComparer.Ordinal)
            .ThenBy(m => m.Timeframe)
            .ThenBy(m => m.RegimeScope ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(m => m.Id)
            .ToList();

        var selected = snapshotBacked
            .Take(maxModels)
            .ToList();

        return new WarmupCandidateSelection(
            selected,
            validRows.Count,
            skippedEmptySnapshot,
            invalidModelCount,
            Math.Max(0, snapshotBacked.Count - selected.Count));
    }

    private static async Task<MLModel?> LoadWarmupModelAsync(
        DbContext readDb,
        long modelId,
        CancellationToken ct)
    {
        return await readDb
            .Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == modelId
                                   && m.IsActive
                                   && !m.IsDeleted
                                   && !m.IsSuppressed
                                   && !m.IsMetaLearner
                                   && !m.IsMamlInitializer
                                   && (m.Status == MLModelStatus.Active || m.IsFallbackChampion),
                ct);
    }

    private static async Task<List<Candle>> LoadWarmupCandlesAsync(
        DbContext readDb,
        string symbol,
        Timeframe timeframe,
        CancellationToken ct)
    {
        var candles = await readDb
            .Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol == symbol
                     && c.Timeframe == timeframe
                     && c.IsClosed
                     && !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .Take(RequiredCandles)
            .ToListAsync(ct);

        candles.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return candles;
    }

    private async Task<string?> GetCurrentRegimeAsync(
        DbContext readDb,
        string symbol,
        Timeframe timeframe,
        CancellationToken ct)
    {
        try
        {
            var snapshot = await readDb
                .Set<MarketRegimeSnapshot>()
                .AsNoTracking()
                .Where(r => r.Symbol == symbol
                         && r.Timeframe == timeframe
                         && !r.IsDeleted)
                .OrderByDescending(r => r.DetectedAt)
                .Select(r => new { r.Regime })
                .FirstOrDefaultAsync(ct);

            return snapshot?.Regime.ToString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "{Worker}: regime lookup failed for {Symbol}/{Timeframe}; warming with global regime context",
                WorkerName,
                symbol,
                timeframe);
            return null;
        }
    }

    private void RecordSummaryMetrics(WarmupStats stats)
    {
        _metrics?.WorkerCycleDurationMs.Record(stats.ElapsedMs, Tag("worker", WorkerName));
        _metrics?.MLInferenceWarmupCycleDurationMs.Record(stats.ElapsedMs);

        if (stats.Warmed > 0)
            _metrics?.MLInferenceWarmupModelsWarmed.Add(stats.Warmed);
        if (stats.Failed > 0)
            _metrics?.MLInferenceWarmupModelsFailed.Add(stats.Failed);
        if (stats.TimedOut > 0)
            _metrics?.MLInferenceWarmupModelTimeouts.Add(stats.TimedOut);
        if (stats.SkippedEmptySnapshot > 0)
            _metrics?.MLInferenceWarmupModelsSkipped.Add(stats.SkippedEmptySnapshot, Tag("reason", "missing_snapshot"));
        if (stats.SkippedNoCandles > 0)
            _metrics?.MLInferenceWarmupModelsSkipped.Add(stats.SkippedNoCandles, Tag("reason", "insufficient_candles"));
        if (stats.SkippedInvalidModel > 0)
            _metrics?.MLInferenceWarmupModelsSkipped.Add(stats.SkippedInvalidModel, Tag("reason", "invalid_model"));
        if (stats.SkippedLimit > 0)
            _metrics?.MLInferenceWarmupModelsSkipped.Add(stats.SkippedLimit, Tag("reason", "max_models_per_startup"));
    }

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLInferenceWarmupCyclesSkipped.Add(1, Tag("reason", reason));

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        if (db.Database.IsRelational())
            db.Database.SetCommandTimeout(seconds);
    }

    private static long ElapsedMs(Stopwatch stopwatch)
        => (long)stopwatch.Elapsed.TotalMilliseconds;

    private static int GetConfigurationInt(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        var raw = configuration[key];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : Math.Clamp(defaultValue, min, max);
    }

    private static int GetInt(
        IReadOnlyDictionary<string, string?> values,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        return values.TryGetValue(key, out var raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : Math.Clamp(defaultValue, min, max);
    }

    private static bool GetBool(
        IReadOnlyDictionary<string, string?> values,
        string key,
        bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        return ParseBool(raw, defaultValue);
    }

    private static bool GetConfigurationBool(
        IConfiguration configuration,
        string key,
        bool defaultValue)
    {
        var raw = configuration[key];
        return string.IsNullOrWhiteSpace(raw)
            ? defaultValue
            : ParseBool(raw, defaultValue);
    }

    private static bool ParseBool(string raw, bool defaultValue)
    {
        if (bool.TryParse(raw, out var parsed))
            return parsed;

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            ? true
            : raw.Equals("0", StringComparison.OrdinalIgnoreCase) ||
              raw.Equals("no", StringComparison.OrdinalIgnoreCase)
                ? false
                : defaultValue;
    }

    private static int NormalizeStartupDelaySeconds(int value)
        => value is >= 0 and <= 300 ? value : 5;

    private static string NormalizeSymbol(string? symbol)
        => string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();

    private static KeyValuePair<string, object?> Tag(string name, object? value)
        => new(name, value);

    internal sealed class WarmupStats
    {
        public int TotalEligibleModels { get; set; }
        public int Warmed { get; set; }
        public int Failed { get; set; }
        public int TimedOut { get; set; }
        public int SkippedNoCandles { get; set; }
        public int SkippedEmptySnapshot { get; set; }
        public int SkippedInvalidModel { get; set; }
        public int SkippedLimit { get; set; }
        public long ElapsedMs { get; set; }
    }

    private sealed record WarmupModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        string? RegimeScope,
        DateTime? ActivatedAt,
        bool HasSnapshot);

    private sealed record WarmupCandidateSelection(
        IReadOnlyList<WarmupModelCandidate> Candidates,
        int TotalEligible,
        int SkippedEmptySnapshot,
        int SkippedInvalidModel,
        int SkippedLimit);

    private sealed record WarmupSettings(
        bool Enabled,
        int StartupDelaySeconds,
        TimeSpan ModelTimeout,
        int MaxModelsPerStartup,
        int MaxTimeoutsBeforeAbort,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds)
    {
        public static WarmupSettings FromSources(
            IConfiguration configuration,
            MLInferenceWarmupOptions options)
        {
            return new WarmupSettings(
                Enabled: GetConfigurationBool(configuration, CK_Enabled, options.Enabled),
                StartupDelaySeconds: GetConfigurationInt(
                    configuration,
                    CK_StartupDelaySeconds,
                    NormalizeStartupDelaySeconds(options.StartupDelaySeconds),
                    0,
                    300),
                ModelTimeout: TimeSpan.FromSeconds(GetConfigurationInt(
                    configuration,
                    CK_ModelTimeoutSeconds,
                    options.ModelTimeoutSeconds,
                    1,
                    600)),
                MaxModelsPerStartup: GetConfigurationInt(
                    configuration,
                    CK_MaxModelsPerStartup,
                    options.MaxModelsPerStartup,
                    1,
                    250_000),
                MaxTimeoutsBeforeAbort: GetConfigurationInt(
                    configuration,
                    CK_MaxTimeoutsBeforeAbort,
                    options.MaxTimeoutsBeforeAbort,
                    0,
                    100),
                LockTimeoutSeconds: GetConfigurationInt(
                    configuration,
                    CK_LockTimeoutSeconds,
                    options.LockTimeoutSeconds,
                    0,
                    300),
                DbCommandTimeoutSeconds: GetConfigurationInt(
                    configuration,
                    CK_DbCommandTimeoutSeconds,
                    options.DbCommandTimeoutSeconds,
                    1,
                    600));
        }
    }
}
