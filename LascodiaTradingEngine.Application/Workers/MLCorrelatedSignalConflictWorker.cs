using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects conflicting ML predictions for strongly correlated currency pairs and raises
/// an alert, preventing the trading engine from simultaneously taking opposing positions
/// that would partially cancel each other out or expose hidden correlated risk.
///
/// <b>Problem:</b> EURUSD and GBPUSD are typically 70–85% correlated. If the EURUSD model
/// predicts Buy while the GBPUSD model predicts Sell within the same evaluation window,
/// at least one prediction is almost certainly wrong. Acting on both signals without
/// investigation compounds directional risk.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>Read the pair correlation map from <c>EngineConfig</c> key
///         <c>MLCorrelation:PairMap</c> — a JSON object where each key is a base symbol
///         and the value is an array of correlated symbols,
///         e.g. <c>{"EURUSD":["GBPUSD","AUDUSD"]}</c>.</item>
///   <item>For each base symbol, load <see cref="TradeSignal"/> records with
///         <c>Status = Approved</c> created within the conflict detection window.</item>
///   <item>For each correlated peer, load its approved signals in the same window.</item>
///   <item>If the most recent approved base signal and the most recent approved peer signal
///         have opposing <c>MLPredictedDirection</c> values, fire an alert.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLCorrelation:PollIntervalSeconds</c>   — default 300 (5 min)</item>
///   <item><c>MLCorrelation:WindowMinutes</c>         — conflict window, default 60</item>
///   <item><c>MLCorrelation:PairMap</c>               — JSON correlation map, default empty</item>
///   <item><c>MLCorrelation:AlertDestination</c>      — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLCorrelatedSignalConflictWorker : BackgroundService
{
    private const string CK_PollSecs   = "MLCorrelation:PollIntervalSeconds";
    private const string CK_Window     = "MLCorrelation:WindowMinutes";
    private const string CK_PairMap    = "MLCorrelation:PairMap";
    private const string CK_AlertDest  = "MLCorrelation:AlertDestination";

    private readonly IServiceScopeFactory                          _scopeFactory;
    private readonly ILogger<MLCorrelatedSignalConflictWorker>     _logger;

    public MLCorrelatedSignalConflictWorker(
        IServiceScopeFactory                           scopeFactory,
        ILogger<MLCorrelatedSignalConflictWorker>      logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLCorrelatedSignalConflictWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 300;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 300, stoppingToken);

                await DetectConflictsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLCorrelatedSignalConflictWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLCorrelatedSignalConflictWorker stopping.");
    }

    // ── Detection core ────────────────────────────────────────────────────────

    private async Task DetectConflictsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowMinutes = await GetConfigAsync<int>   (readCtx, CK_Window,    60,      ct);
        string pairMapJson   = await GetConfigAsync<string>(readCtx, CK_PairMap,   "{}",    ct);
        string alertDest     = await GetConfigAsync<string>(readCtx, CK_AlertDest, "ml-ops", ct);

        // Parse pair correlation map
        Dictionary<string, List<string>> pairMap;
        try
        {
            pairMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                pairMapJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MLCorrelation: failed to parse PairMap JSON — skipping.");
            return;
        }

        if (pairMap.Count == 0)
        {
            _logger.LogDebug("MLCorrelation: PairMap is empty, nothing to check.");
            return;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-windowMinutes);
        var allSymbols = pairMap.Keys
            .Concat(pairMap.Values.SelectMany(v => v))
            .Distinct()
            .ToList();

        // Batch-load latest approved signals for all relevant symbols
        var latestSignals = await readCtx.Set<TradeSignal>()
            .Where(ts => allSymbols.Contains(ts.Symbol)            &&
                         ts.Status == TradeSignalStatus.Approved    &&
                         ts.GeneratedAt >= cutoff                   &&
                         ts.MLPredictedDirection.HasValue           &&
                         !ts.IsDeleted)
            .AsNoTracking()
            .Select(ts => new
            {
                ts.Symbol,
                ts.MLPredictedDirection,
                ts.GeneratedAt,
            })
            .ToListAsync(ct);

        // Build symbol → latest predicted direction map
        var directionMap = latestSignals
            .GroupBy(s => s.Symbol)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.GeneratedAt).First().MLPredictedDirection!.Value);

        foreach (var (baseSymbol, peers) in pairMap)
        {
            ct.ThrowIfCancellationRequested();

            if (!directionMap.TryGetValue(baseSymbol, out var baseDir)) continue;

            foreach (var peer in peers)
            {
                if (!directionMap.TryGetValue(peer, out var peerDir)) continue;

                if (baseDir == peerDir) continue; // same direction — no conflict

                _logger.LogWarning(
                    "MLCorrelation: conflicting signals — {Base} predicts {BaseDir} " +
                    "but correlated pair {Peer} predicts {PeerDir} within {Window} min window.",
                    baseSymbol, baseDir, peer, peerDir, windowMinutes);

                // Avoid duplicating an already-active conflict alert
                bool alertExists = await readCtx.Set<Alert>()
                    .AnyAsync(a => (a.Symbol == baseSymbol || a.Symbol == peer) &&
                                   a.AlertType == AlertType.MLModelDegraded     &&
                                   a.IsActive  && !a.IsDeleted, ct);

                if (alertExists) continue;

                writeCtx.Set<Alert>().Add(new Alert
                {
                    AlertType     = AlertType.MLModelDegraded,
                    Symbol        = baseSymbol,
                    Channel       = AlertChannel.Webhook,
                    Destination   = alertDest,
                    ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        reason         = "correlated_signal_conflict",
                        severity       = "warning",
                        baseSymbol,
                        peerSymbol     = peer,
                        baseDirection  = baseDir.ToString(),
                        peerDirection  = peerDir.ToString(),
                        windowMinutes,
                    }),
                    IsActive = true,
                });
            }
        }

        await writeCtx.SaveChangesAsync(ct);
    }

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
