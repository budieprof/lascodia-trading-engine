using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
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

    private const string CK_Enabled                = "MLWarmup:InferenceWarmupEnabled";
    private const string CK_StartupDelaySeconds    = "MLWarmup:StartupDelaySeconds";
    private const string CK_ModelTimeoutSeconds    = "MLWarmup:ModelTimeoutSeconds";
    private const string CK_MaxModelsPerStartup    = "MLWarmup:MaxModelsPerStartup";
    private const string CK_MaxTimeoutsBeforeAbort = "MLWarmup:MaxTimeoutsBeforeAbort";

    private const bool DefaultEnabled = true;
    private const int DefaultStartupDelaySeconds = 5;
    private const int DefaultModelTimeoutSeconds = 30;
    private const int DefaultMaxModelsPerStartup = 10_000;
    private const int DefaultMaxTimeoutsBeforeAbort = 1;

    private readonly ILogger<MLInferenceWarmupWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;

    public MLInferenceWarmupWorker(
        ILogger<MLInferenceWarmupWorker> logger,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _logger        = logger;
        _scopeFactory  = scopeFactory;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var startupDelaySeconds = GetConfigurationInt(
                _configuration,
                CK_StartupDelaySeconds,
                DefaultStartupDelaySeconds,
                min: 0,
                max: 300);

            if (startupDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(startupDelaySeconds), stoppingToken);

            await WarmupAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("MLInferenceWarmupWorker: startup warm-up cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MLInferenceWarmupWorker: warm-up aborted due to critical error");
        }
    }

    private async Task WarmupAsync(CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();

        int warmed = 0, failed = 0, timedOut = 0, skippedNoCandles = 0,
            skippedEmptySnapshot = 0, skippedLimit = 0;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var warmupScorer = scope.ServiceProvider.GetRequiredService<IMLModelWarmupScorer>();
        var readDb       = readCtx.GetDbContext();

        var settings = await LoadSettingsAsync(readDb, stoppingToken);
        if (!settings.Enabled)
        {
            _logger.LogInformation("MLInferenceWarmupWorker: disabled by {ConfigKey}", CK_Enabled);
            return;
        }

        _logger.LogInformation(
            "MLInferenceWarmupWorker: warming active ML model core inference paths (maxModels={MaxModels}, modelTimeout={Timeout}s)",
            settings.MaxModelsPerStartup, settings.ModelTimeout.TotalSeconds);

        var (activeModels, totalActiveModels) = await LoadWarmupCandidatesAsync(
            readDb, settings.MaxModelsPerStartup, stoppingToken);
        skippedLimit = Math.Max(0, totalActiveModels - activeModels.Count);

        var candidatesWithSnapshots = activeModels
            .Where(m => m.HasSnapshot)
            .ToList();

        skippedEmptySnapshot = activeModels.Count - candidatesWithSnapshots.Count;

        _logger.LogInformation(
            "MLInferenceWarmupWorker: found {Count} warm-up candidates with snapshots across {GroupCount} symbol/timeframe groups (active={Active}, skippedLimit={SkippedLimit}, skippedNoSnapshot={SkippedNoSnapshot})",
            candidatesWithSnapshots.Count,
            candidatesWithSnapshots.Select(m => (m.Symbol, m.Timeframe)).Distinct().Count(),
            totalActiveModels,
            skippedLimit,
            skippedEmptySnapshot);

        bool abortWarmup = false;
        foreach (var group in candidatesWithSnapshots.GroupBy(m => (m.Symbol, m.Timeframe)))
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (abortWarmup) break;

            var (symbol, timeframe) = group.Key;
            var candles = await LoadWarmupCandlesAsync(readDb, symbol, timeframe, stoppingToken);

            if (candles.Count < RequiredCandles)
            {
                skippedNoCandles += group.Count();
                _logger.LogWarning(
                    "MLInferenceWarmupWorker: skipping {Symbol}/{Timeframe} models — only {Count}/{Required} closed candles available",
                    symbol, timeframe, candles.Count, RequiredCandles);
                continue;
            }

            var currentRegime = await GetCurrentRegimeAsync(readDb, symbol, timeframe, stoppingToken);

            foreach (var candidate in group.OrderBy(m => m.RegimeScope ?? string.Empty).ThenBy(m => m.Id))
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
                        skippedEmptySnapshot++;
                        _logger.LogWarning(
                            "MLInferenceWarmupWorker: skipping model {ModelId} ({Symbol}/{Timeframe}) because its snapshot disappeared or is empty",
                            candidate.Id, candidate.Symbol, candidate.Timeframe);
                        continue;
                    }

                    var regimeForWarmup = model.RegimeScope ?? currentRegime;
                    await warmupScorer.WarmupModelAsync(
                        model, candles, regimeForWarmup, modelCts.Token);

                    warmed++;
                    _logger.LogDebug(
                        "MLInferenceWarmupWorker: warmed model {ModelId} ({Symbol}/{Timeframe}, regime={Regime}) in {Elapsed}ms",
                        model.Id, model.Symbol, model.Timeframe, regimeForWarmup ?? "global",
                        modelSw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (modelCts.IsCancellationRequested)
                {
                    timedOut++;
                    _logger.LogWarning(
                        "MLInferenceWarmupWorker: timed out warming model {ModelId} ({Symbol}/{Timeframe}) after {Timeout}s",
                        candidate.Id, candidate.Symbol, candidate.Timeframe, settings.ModelTimeout.TotalSeconds);

                    if (settings.MaxTimeoutsBeforeAbort > 0 &&
                        timedOut >= settings.MaxTimeoutsBeforeAbort)
                    {
                        abortWarmup = true;
                        _logger.LogWarning(
                            "MLInferenceWarmupWorker: aborting remaining startup warm-up after {TimedOut} timeout(s) to avoid background inference pile-up",
                            timedOut);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex,
                        "MLInferenceWarmupWorker: warm-up failed for model {ModelId} ({Symbol}/{Timeframe}). " +
                        "Model may have a stale snapshot, invalid weights, or incompatible feature schema.",
                        candidate.Id, candidate.Symbol, candidate.Timeframe);
                }
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "MLInferenceWarmupWorker: completed in {Elapsed}ms — warmed={Warmed}, failed={Failed}, timedOut={TimedOut}, skippedNoCandles={SkippedNoCandles}, skippedEmptySnapshot={SkippedEmptySnapshot}, skippedLimit={SkippedLimit}",
            sw.ElapsedMilliseconds, warmed, failed, timedOut, skippedNoCandles, skippedEmptySnapshot, skippedLimit);

        if (failed + timedOut > 0)
            _logger.LogWarning(
                "MLInferenceWarmupWorker: {ProblemCount} models did not complete warm-up — check model snapshots, feature schema, and inference engine logs",
                failed + timedOut);
    }

    private async Task<WarmupSettings> LoadSettingsAsync(
        DbContext readDb,
        CancellationToken ct)
    {
        var fallback = WarmupSettings.FromConfiguration(_configuration);
        var keys = new[]
        {
            CK_Enabled,
            CK_ModelTimeoutSeconds,
            CK_MaxModelsPerStartup,
            CK_MaxTimeoutsBeforeAbort
        };

        Dictionary<string, string?> values;
        try
        {
            values = await readDb
                .Set<EngineConfig>()
                .AsNoTracking()
                .Where(c => keys.Contains(c.Key) && !c.IsDeleted)
                .Select(c => new { c.Key, Value = (string?)c.Value })
                .ToDictionaryAsync(c => c.Key, c => c.Value, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "MLInferenceWarmupWorker: failed to read EngineConfig warm-up settings; using application configuration/defaults");
            values = new Dictionary<string, string?>();
        }

        return new WarmupSettings(
            Enabled: GetBool(values, CK_Enabled, fallback.Enabled),
            ModelTimeout: TimeSpan.FromSeconds(GetInt(
                values, CK_ModelTimeoutSeconds, (int)fallback.ModelTimeout.TotalSeconds, 1, 600)),
            MaxModelsPerStartup: GetInt(
                values, CK_MaxModelsPerStartup, fallback.MaxModelsPerStartup, 1, 250_000),
            MaxTimeoutsBeforeAbort: GetInt(
                values, CK_MaxTimeoutsBeforeAbort, fallback.MaxTimeoutsBeforeAbort, 0, 100));
    }

    private async Task<(List<WarmupModelCandidate> Candidates, int TotalActive)> LoadWarmupCandidatesAsync(
        DbContext readDb,
        int maxModels,
        CancellationToken ct)
    {
        var query = readDb
            .Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                     && !m.IsDeleted
                     && m.Status != MLModelStatus.Failed);

        var totalActive = await query.CountAsync(ct);
        var candidates = await query
            .OrderBy(m => m.Symbol)
            .ThenBy(m => m.Timeframe)
            .ThenBy(m => m.RegimeScope)
            .ThenBy(m => m.Id)
            .Select(m => new WarmupModelCandidate(
                m.Id,
                m.Symbol,
                m.Timeframe,
                m.RegimeScope,
                m.ModelBytes != null))
            .Take(maxModels)
            .ToListAsync(ct);

        return (candidates, totalActive);
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
                                   && m.Status != MLModelStatus.Failed,
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
                "MLInferenceWarmupWorker: regime lookup failed for {Symbol}/{Timeframe}; warming with global regime context",
                symbol, timeframe);
            return null;
        }
    }

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
            : defaultValue;
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
            : defaultValue;
    }

    private static bool GetBool(
        IReadOnlyDictionary<string, string?> values,
        string key,
        bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

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

    private sealed record WarmupModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        string? RegimeScope,
        bool HasSnapshot);

    private sealed record WarmupSettings(
        bool Enabled,
        TimeSpan ModelTimeout,
        int MaxModelsPerStartup,
        int MaxTimeoutsBeforeAbort)
    {
        public static WarmupSettings FromConfiguration(IConfiguration configuration)
        {
            return new WarmupSettings(
                Enabled: GetConfigurationBool(configuration, CK_Enabled, DefaultEnabled),
                ModelTimeout: TimeSpan.FromSeconds(GetConfigurationInt(
                    configuration, CK_ModelTimeoutSeconds, DefaultModelTimeoutSeconds, 1, 600)),
                MaxModelsPerStartup: GetConfigurationInt(
                    configuration, CK_MaxModelsPerStartup, DefaultMaxModelsPerStartup, 1, 250_000),
                MaxTimeoutsBeforeAbort: GetConfigurationInt(
                    configuration, CK_MaxTimeoutsBeforeAbort, DefaultMaxTimeoutsBeforeAbort, 0, 100));
        }

        private static bool GetConfigurationBool(
            IConfiguration configuration,
            string key,
            bool defaultValue)
        {
            var raw = configuration[key];
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;

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
    }
}
