using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Closes the ML feedback loop by resolving the actual trade outcome for every
/// <see cref="MLModelPredictionLog"/> record that still has <c>DirectionCorrect == null</c>.
///
/// <para>
/// This is the sole outcome-resolution worker (replaces <c>PredictionOutcomeWorker</c> which
/// was removed). It uses the next closed candle after the prediction timestamp as the
/// ground truth, which is always available once the candle closes.
/// </para>
///
/// Resolution algorithm:
/// <list type="number">
///   <item>Find unresolved logs older than one candle duration (timeframe-aware cutoff).</item>
///   <item>Group by symbol/timeframe and batch-load the candles covering each group's window.</item>
///   <item>For each log, find the closest preceding closed candle (<c>prevCandle</c>) and the
///         first closed candle strictly after <c>PredictedAt</c> (<c>outcomeCandle</c>).</item>
///   <item><b>Gap detection:</b> if the gap between <c>prevCandle</c> and <c>outcomeCandle</c>
///         exceeds <c>MaxCandleGapFactor × TimeframeExpectedGap</c> (e.g. weekend/holiday),
///         mark <c>ResolutionSource = "GapSkipped"</c> and leave <c>DirectionCorrect = null</c>
///         so the log is excluded from accuracy calculations.</item>
///   <item>Resolve <c>ActualDirection</c>, <c>ActualMagnitudePips</c>, <c>DirectionCorrect</c>,
///         and <c>ResolutionSource = "NextBarCandle"</c> via <c>ExecuteUpdateAsync</c>.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLOutcome:PollIntervalSeconds</c>  — default 120 (2 min)</item>
///   <item><c>MLOutcome:BatchSize</c>             — default 200</item>
///   <item><c>MLOutcome:MaxCandleGapFactor</c>    — default 3.0 (3× expected gap triggers skip)</item>
/// </list>
/// </summary>
public sealed class MLPredictionOutcomeWorker : BackgroundService
{
    private const string CK_PollSecs      = "MLOutcome:PollIntervalSeconds";
    private const string CK_BatchSize     = "MLOutcome:BatchSize";
    private const string CK_MaxGapFactor  = "MLOutcome:MaxCandleGapFactor";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLPredictionOutcomeWorker> _logger;

    public MLPredictionOutcomeWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLPredictionOutcomeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPredictionOutcomeWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 120;

            try
            {
                await using var scope    = _scopeFactory.CreateAsyncScope();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs       = await GetConfigAsync<int>   (ctx, CK_PollSecs,     120, stoppingToken);
                int    batch   = await GetConfigAsync<int>   (ctx, CK_BatchSize,    200, stoppingToken);
                double gapFact = await GetConfigAsync<double>(ctx, CK_MaxGapFactor, 3.0, stoppingToken);

                int resolved = await ResolveOutcomesAsync(ctx, writeCtx, batch, gapFact, stoppingToken);
                int orphaned = await ResolveOrphanedLogsAsync(ctx, writeCtx, batch, stoppingToken);

                if (resolved > 0 || orphaned > 0)
                    _logger.LogInformation(
                        "MLPredictionOutcomeWorker: resolved {N} outcomes, marked {O} orphaned.", resolved, orphaned);
                else
                    _logger.LogDebug("No unresolved prediction logs found.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLPredictionOutcomeWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLPredictionOutcomeWorker stopping.");
    }

    // ── Resolution core ───────────────────────────────────────────────────────

    private async Task<int> ResolveOutcomesAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     batchSize,
        double                                  maxGapFactor,
        CancellationToken                       ct)
    {
        // Use a conservative cutoff to ensure at least one full candle has closed.
        // Timeframe-specific minimums prevent resolving before the outcome candle exists.
        // We fetch unresolved logs and filter per-log after grouping by timeframe.
        var absoluteCutoff = DateTime.UtcNow.AddMinutes(-5); // minimum safety buffer

        var unresolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => !l.IsDeleted &&
                        l.DirectionCorrect == null &&
                        l.PredictedAt      <= absoluteCutoff)
            .OrderBy(l => l.PredictedAt)
            .Take(batchSize)
            .AsNoTracking()
            .ToListAsync(ct);

        if (unresolved.Count == 0) return 0;

        // Group by symbol/timeframe for efficient candle queries
        var groups = unresolved
            .GroupBy(l => (l.Symbol, l.Timeframe))
            .ToList();

        int totalResolved = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            var (symbol, timeframe) = group.Key;

            // Apply timeframe-aware cutoff so we only resolve logs where at least
            // one full candle has closed since the prediction was made.
            var timeframeCutoff = DateTime.UtcNow.Subtract(TimeframeMinDuration(timeframe));
            var logsInGroup = group
                .Where(l => l.PredictedAt <= timeframeCutoff)
                .OrderBy(l => l.PredictedAt)
                .ToList();

            if (logsInGroup.Count == 0) continue;

            var earliest = logsInGroup.First().PredictedAt;
            var latest   = logsInGroup.Last().PredictedAt;

            // Load a candle window that spans all logs in the group (with buffer on both sides)
            var candles = await readCtx.Set<Candle>()
                .Where(c => c.Symbol    == symbol    &&
                            c.Timeframe == timeframe  &&
                            c.Timestamp >= earliest.AddHours(-2) &&
                            c.Timestamp <= latest.AddHours(4)    &&
                            c.IsClosed)
                .OrderBy(c => c.Timestamp)
                .AsNoTracking()
                .ToListAsync(ct);

            if (candles.Count < 2) continue;

            foreach (var log in logsInGroup)
            {
                // The candle that was current when the prediction was made
                var prevCandle = candles.LastOrDefault(c => c.Timestamp <= log.PredictedAt);

                // The first fully-closed candle AFTER the prediction
                var outcomeCandle = candles.FirstOrDefault(c => c.Timestamp > log.PredictedAt);

                if (prevCandle is null || outcomeCandle is null) continue;

                // ── Gap detection ──────────────────────────────────────────────
                // If the candle gap exceeds maxGapFactor × expected interval (e.g. a weekend or
                // holiday break), the outcome would be contaminated by the gap move and should not
                // be used for accuracy calculations. Mark as GapSkipped and leave DirectionCorrect null.
                var    expectedGap    = TimeframeExpectedGap(timeframe);
                double actualGapMins  = (outcomeCandle.Timestamp - prevCandle.Timestamp).TotalMinutes;
                double maxGapMins     = expectedGap.TotalMinutes * maxGapFactor;

                if (actualGapMins > maxGapMins)
                {
                    await writeCtx.Set<MLModelPredictionLog>()
                        .Where(l => l.Id == log.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(l => l.OutcomeRecordedAt, DateTime.UtcNow)
                            .SetProperty(l => l.ResolutionSource,  "GapSkipped"),
                            ct);

                    _logger.LogDebug(
                        "GapSkipped log {Id} ({Symbol}/{Tf}): gap {Gap:F0} min > {Max:F0} min ({Factor}× expected).",
                        log.Id, symbol, timeframe, actualGapMins, maxGapMins, maxGapFactor);

                    totalResolved++;
                    continue;
                }

                bool priceWentUp      = outcomeCandle.Close > prevCandle.Close;
                var  actualDirection  = priceWentUp ? TradeDirection.Buy : TradeDirection.Sell;
                decimal priceDiff     = outcomeCandle.Close - prevCandle.Close;
                bool directionCorrect = log.PredictedDirection == actualDirection;

                await writeCtx.Set<MLModelPredictionLog>()
                    .Where(l => l.Id == log.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.ActualDirection,     actualDirection)
                        .SetProperty(l => l.ActualMagnitudePips, priceDiff)
                        .SetProperty(l => l.DirectionCorrect,    directionCorrect)
                        .SetProperty(l => l.OutcomeRecordedAt,   DateTime.UtcNow)
                        .SetProperty(l => l.ResolutionSource,    "NextBarCandle"),
                        ct);

                totalResolved++;

                _logger.LogDebug(
                    "Resolved log {Id} ({Symbol}/{Tf}): predicted={Pred} actual={Act} correct={OK}",
                    log.Id, symbol, timeframe, log.PredictedDirection, actualDirection, directionCorrect);
            }
        }

        return totalResolved;
    }

    // ── Orphan resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Marks prediction logs as <c>ResolutionSource = "Orphaned"</c> when their associated
    /// <see cref="TradeSignal"/> has been <c>Rejected</c> or <c>Expired</c>.
    ///
    /// These logs can never receive a real outcome because no trade was ever executed against
    /// them. Leaving them as null-<c>DirectionCorrect</c> permanently inflates the apparent
    /// "pending" count and, if included naively, would undercount accuracy denominators.
    /// Marking them "Orphaned" excludes them from all accuracy workers while still preserving
    /// the raw record for audit purposes.
    /// </summary>
    private static async Task<int> ResolveOrphanedLogsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     batchSize,
        CancellationToken                       ct)
    {
        // Find prediction logs that are unresolved and whose signal will never execute
        var orphanedIds = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => !l.IsDeleted                &&
                        l.DirectionCorrect == null  &&
                        l.ResolutionSource == null)
            .Join(readCtx.Set<TradeSignal>(),
                  l  => l.TradeSignalId,
                  ts => ts.Id,
                  (l, ts) => new { LogId = l.Id, ts.Status })
            .Where(x => x.Status == TradeSignalStatus.Rejected ||
                        x.Status == TradeSignalStatus.Expired)
            .OrderBy(x => x.LogId)
            .Take(batchSize)
            .Select(x => x.LogId)
            .ToListAsync(ct);

        if (orphanedIds.Count == 0) return 0;

        int marked = await writeCtx.Set<MLModelPredictionLog>()
            .Where(l => orphanedIds.Contains(l.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.OutcomeRecordedAt, DateTime.UtcNow)
                .SetProperty(l => l.ResolutionSource,  "Orphaned"),
                ct);

        return marked;
    }

    // ── Timeframe helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the minimum elapsed time after a prediction before we can be confident
    /// that at least one full candle has closed for the given timeframe.
    /// Ensures outcome resolution waits for a meaningful price movement to occur.
    /// </summary>
    private static TimeSpan TimeframeMinDuration(Timeframe tf) => tf switch
    {
        Timeframe.M1  => TimeSpan.FromMinutes(2),
        Timeframe.M5  => TimeSpan.FromMinutes(6),
        Timeframe.M15 => TimeSpan.FromMinutes(20),
        Timeframe.H1  => TimeSpan.FromMinutes(70),
        Timeframe.H4  => TimeSpan.FromHours(5),
        Timeframe.D1  => TimeSpan.FromHours(26),
        _             => TimeSpan.FromMinutes(70),
    };

    /// <summary>
    /// Returns the nominal candle interval for gap-detection purposes.
    /// A consecutive-candle gap larger than <c>MaxCandleGapFactor × TimeframeExpectedGap</c>
    /// is treated as a market closure (weekend, holiday) and the log is marked
    /// <c>ResolutionSource = "GapSkipped"</c>.
    /// </summary>
    private static TimeSpan TimeframeExpectedGap(Timeframe tf) => tf switch
    {
        Timeframe.M1  => TimeSpan.FromMinutes(1),
        Timeframe.M5  => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.H1  => TimeSpan.FromHours(1),
        Timeframe.H4  => TimeSpan.FromHours(4),
        Timeframe.D1  => TimeSpan.FromHours(24),
        _             => TimeSpan.FromHours(1),
    };

    // ── Config helper ─────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
