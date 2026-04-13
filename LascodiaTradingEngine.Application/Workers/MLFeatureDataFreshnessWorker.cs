using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Application.Services.Alerts;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects when ML feature data sources go stale by checking the freshness of
/// COT reports, sentiment snapshots, and per-symbol/timeframe candle data.
///
/// <para>
/// Stale input features are a silent failure mode — the model continues scoring
/// but its predictions degrade because one or more feature groups have stopped
/// updating. This worker catches the problem early and writes flags to
/// <see cref="EngineConfig"/> so downstream consumers (trainers, scorers) can
/// gracefully degrade or abstain.
/// </para>
///
/// Checks:
/// <list type="bullet">
///   <item><b>COT data</b>: most recent <see cref="COTReport"/> older than
///         <c>MLFeatureStale:MaxCotAgeDays</c> (default 10).</item>
///   <item><b>Sentiment data</b>: most recent <see cref="SentimentSnapshot"/> older
///         than <c>MLFeatureStale:MaxSentimentAgeHours</c> (default 24).</item>
///   <item><b>Candle data</b>: for each active model, the most recent candle for its
///         symbol/timeframe older than 3× the timeframe interval.</item>
/// </list>
///
/// For each stale source, writes:
/// <list type="bullet">
///   <item><c>MLFeatureStale:{Source}:IsStale</c> = "true" / "false"</item>
///   <item><c>MLFeatureStale:{Source}:LastSeenAt</c> = ISO 8601 timestamp</item>
/// </list>
/// and creates an <see cref="Alert"/> with <see cref="AlertType.MLModelDegraded"/>.
/// </summary>
public sealed class MLFeatureDataFreshnessWorker : BackgroundService
{
    // ── Config keys ────────────────────────────────────────────────────────────
    private const string CK_PollSecs            = "MLFeatureStale:PollIntervalSeconds";
    private const string CK_MaxCotAgeDays       = "MLFeatureStale:MaxCotAgeDays";
    private const string CK_MaxSentimentAgeHrs  = "MLFeatureStale:MaxSentimentAgeHours";
    private const string CK_CandleStaleMult     = "MLFeatureStale:CandleStaleMultiplier";
    private const string CK_AlertDestination    = "MLFeatureStale:AlertDestination";

    private readonly IServiceScopeFactory                   _scopeFactory;
    private readonly ILogger<MLFeatureDataFreshnessWorker>  _logger;

    public MLFeatureDataFreshnessWorker(
        IServiceScopeFactory                  scopeFactory,
        ILogger<MLFeatureDataFreshnessWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureDataFreshnessWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 1800; // default 30 minutes

            try
            {
                await using var scope    = _scopeFactory.CreateAsyncScope();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var readCtx  = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(readCtx, CK_PollSecs, 1800, stoppingToken);

                int    maxCotAgeDays       = await GetConfigAsync<int>   (readCtx, CK_MaxCotAgeDays,      10,   stoppingToken);
                int    maxSentimentAgeHrs  = await GetConfigAsync<int>   (readCtx, CK_MaxSentimentAgeHrs, 24,   stoppingToken);
                double candleStaleMult     = await GetConfigAsync<double>(readCtx, CK_CandleStaleMult,    3.0,  stoppingToken);
                string alertDest           = await GetConfigAsync<string>(readCtx, CK_AlertDestination,   "",   stoppingToken);
                int    alertCooldown       = await GetConfigAsync<int>   (readCtx, AlertCooldownDefaults.CK_MLMonitoring, AlertCooldownDefaults.Default_MLMonitoring, stoppingToken);

                var now = DateTime.UtcNow;
                bool anyStale = false;

                // ── 1. COT data freshness ────────────────────────────────────────
                anyStale |= await CheckCotFreshnessAsync(readCtx, writeCtx, now, maxCotAgeDays, alertDest, alertCooldown, stoppingToken);

                // ── 2. Sentiment data freshness ──────────────────────────────────
                anyStale |= await CheckSentimentFreshnessAsync(readCtx, writeCtx, now, maxSentimentAgeHrs, alertDest, alertCooldown, stoppingToken);

                // ── 3. Per-model candle data freshness ───────────────────────────
                anyStale |= await CheckCandleFreshnessAsync(readCtx, writeCtx, now, candleStaleMult, alertDest, alertCooldown, stoppingToken);

                if (anyStale)
                    await writeCtx.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLFeatureDataFreshnessWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLFeatureDataFreshnessWorker stopping.");
    }

    // ── COT freshness ─────────────────────────────────────────────────────────

    private async Task<bool> CheckCotFreshnessAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        DateTime          now,
        int               maxAgeDays,
        string            alertDest,
        int               alertCooldown,
        CancellationToken ct)
    {
        var latestCot = await readCtx.Set<COTReport>()
            .AsNoTracking()
            .OrderByDescending(c => c.ReportDate)
            .FirstOrDefaultAsync(ct);

        bool isStale;
        string lastSeenAt;

        if (latestCot == null)
        {
            isStale    = true;
            lastSeenAt = "never";
        }
        else
        {
            isStale    = (now - latestCot.ReportDate).TotalDays > maxAgeDays;
            lastSeenAt = latestCot.ReportDate.ToString("o");
        }

        await UpsertConfigAsync(writeCtx, "MLFeatureStale:COT:IsStale",    isStale.ToString(), ct);
        await UpsertConfigAsync(writeCtx, "MLFeatureStale:COT:LastSeenAt", lastSeenAt,         ct);

        if (isStale)
        {
            _logger.LogWarning(
                "COT data is stale (last seen {LastSeen}, max age {MaxDays}d).",
                lastSeenAt, maxAgeDays);

            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                ConditionJson = JsonSerializer.Serialize(new
                {
                    Source    = "COT",
                    LastSeen  = lastSeenAt,
                    MaxAgeDays = maxAgeDays,
                }),
                DeduplicationKey = "MLFeatureStale:COT",
                CooldownSeconds  = alertCooldown,
            });

            return true;
        }

        return false;
    }

    // ── Sentiment freshness ───────────────────────────────────────────────────

    private async Task<bool> CheckSentimentFreshnessAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        DateTime          now,
        int               maxAgeHours,
        string            alertDest,
        int               alertCooldown,
        CancellationToken ct)
    {
        var latestSentiment = await readCtx.Set<SentimentSnapshot>()
            .AsNoTracking()
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefaultAsync(ct);

        bool isStale;
        string lastSeenAt;

        if (latestSentiment == null)
        {
            isStale    = true;
            lastSeenAt = "never";
        }
        else
        {
            isStale    = (now - latestSentiment.CapturedAt).TotalHours > maxAgeHours;
            lastSeenAt = latestSentiment.CapturedAt.ToString("o");
        }

        await UpsertConfigAsync(writeCtx, "MLFeatureStale:Sentiment:IsStale",    isStale.ToString(), ct);
        await UpsertConfigAsync(writeCtx, "MLFeatureStale:Sentiment:LastSeenAt", lastSeenAt,         ct);

        if (isStale)
        {
            _logger.LogWarning(
                "Sentiment data is stale (last seen {LastSeen}, max age {MaxHours}h).",
                lastSeenAt, maxAgeHours);

            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                ConditionJson = JsonSerializer.Serialize(new
                {
                    Source       = "Sentiment",
                    LastSeen     = lastSeenAt,
                    MaxAgeHours  = maxAgeHours,
                }),
                DeduplicationKey = "MLFeatureStale:Sentiment",
                CooldownSeconds  = alertCooldown,
            });

            return true;
        }

        return false;
    }

    // ── Candle freshness per active model ─────────────────────────────────────

    private async Task<bool> CheckCandleFreshnessAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        DateTime          now,
        double            staleMultiplier,
        string            alertDest,
        int               alertCooldown,
        CancellationToken ct)
    {
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        bool anyStale = false;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            var latestCandle = await readCtx.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe && c.IsClosed)
                .OrderByDescending(c => c.Timestamp)
                .FirstOrDefaultAsync(ct);

            double expectedIntervalMinutes = TimeframeToMinutes(model.Timeframe) * staleMultiplier;

            bool isStale;
            string lastSeenAt;

            if (latestCandle == null)
            {
                isStale    = true;
                lastSeenAt = "never";
            }
            else
            {
                isStale    = (now - latestCandle.Timestamp).TotalMinutes > expectedIntervalMinutes;
                lastSeenAt = latestCandle.Timestamp.ToString("o");
            }

            string sourceKey = $"Candle:{model.Symbol}:{model.Timeframe}";
            await UpsertConfigAsync(writeCtx, $"MLFeatureStale:{sourceKey}:IsStale",    isStale.ToString(), ct);
            await UpsertConfigAsync(writeCtx, $"MLFeatureStale:{sourceKey}:LastSeenAt", lastSeenAt,         ct);

            if (isStale)
            {
                _logger.LogWarning(
                    "Candle data stale for {Symbol}/{Tf} (last seen {LastSeen}, threshold {Mins:F0}min).",
                    model.Symbol, model.Timeframe, lastSeenAt, expectedIntervalMinutes);

                writeCtx.Set<Alert>().Add(new Alert
                {
                    Symbol        = model.Symbol,
                    AlertType     = AlertType.MLModelDegraded,
                    ConditionJson = JsonSerializer.Serialize(new
                    {
                        Source     = "Candle",
                        Symbol    = model.Symbol,
                        Timeframe = model.Timeframe.ToString(),
                        LastSeen  = lastSeenAt,
                        ThresholdMinutes = expectedIntervalMinutes,
                    }),
                    DeduplicationKey = $"MLFeatureStale:{sourceKey}",
                    CooldownSeconds  = alertCooldown,
                });

                anyStale = true;
            }
        }

        return anyStale;
    }

    // ── Timeframe to minutes mapping ──────────────────────────────────────────

    private static double TimeframeToMinutes(Timeframe tf) => tf switch
    {
        Timeframe.M1  => 1,
        Timeframe.M5  => 5,
        Timeframe.M15 => 15,
        Timeframe.H1  => 60,
        Timeframe.H4  => 240,
        Timeframe.D1  => 1440,
        _             => 60,
    };

    // ── Config helpers ────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        DbContext         ctx,
        string            key,
        T                 defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    private static async Task UpsertConfigAsync(
        DbContext         writeCtx,
        string            key,
        string            value,
        CancellationToken ct)
    {
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Value, value)
                .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow), ct);

        if (rows == 0)
        {
            writeCtx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key             = key,
                Value           = value,
                DataType        = ConfigDataType.String,
                Description     = $"Feature staleness flag written by MLFeatureDataFreshnessWorker.",
                IsHotReloadable = true,
                LastUpdatedAt   = DateTime.UtcNow,
            });
        }
    }
}
