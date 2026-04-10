using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Ratchets trailing stop levels on open positions as price moves in the favourable direction.
/// Runs every 10 seconds alongside <see cref="PositionWorker"/>, which handles the actual
/// SL/TP closure once the updated level is breached.
///
/// Supported algorithms (via <see cref="TrailingStopType"/>):
/// <list type="bullet">
///   <item><description>
///     <b>FixedPips</b> — SL trails price at a fixed pip distance
///     (<c>TrailingStopValue</c> expressed in pips; converted to price by ÷ 10 000).
///   </description></item>
///   <item><description>
///     <b>Percentage</b> — SL trails at <c>TrailingStopValue</c> % of current price.
///   </description></item>
///   <item><description>
///     <b>ATR</b> — SL trails at <c>TrailingStopValue</c> × 14-period ATR of recent closed candles.
///     Falls back to FixedPips distance when fewer than 2 candles are available.
///   </description></item>
/// </list>
///
/// The SL is a one-way ratchet: it only moves in the favourable direction and never widens
/// the risk on an existing trade.
/// </summary>
public sealed class TrailingStopWorker : BackgroundService
{
    private const string CK_PollSecs = "TrailingStop:PollIntervalSeconds";

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory        _scopeFactory;
    private readonly ILivePriceCache             _priceCache;
    private readonly ILogger<TrailingStopWorker> _logger;
    private int _consecutiveFailures;

    public TrailingStopWorker(
        IServiceScopeFactory        scopeFactory,
        ILivePriceCache             priceCache,
        ILogger<TrailingStopWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _priceCache   = priceCache;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TrailingStopWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 10;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 10, stoppingToken);

                await UpdateTrailingStopsAsync(ctx, writeCtx, stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex,
                    "TrailingStopWorker: loop error (consecutive failures: {Failures})",
                    _consecutiveFailures);
            }

            // Exponential backoff on consecutive failures, capped at 5min
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    pollSecs * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : TimeSpan.FromSeconds(pollSecs);

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("TrailingStopWorker stopping.");
    }

    // ── Core update loop ──────────────────────────────────────────────────────

    private async Task UpdateTrailingStopsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        var positions = await readCtx.Set<Position>()
            .Where(p => p.Status           == PositionStatus.Open &&
                        p.TrailingStopEnabled                     &&
                        p.TrailingStopValue != null               &&
                        p.TrailingStopType  != null               &&
                        !p.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        if (positions.Count == 0) return;

        _logger.LogDebug("TrailingStopWorker: processing {Count} trailing-stop position(s).", positions.Count);

        // Pre-compute ATR for each symbol that needs it (batch to avoid N+1 queries)
        var atrSymbols = positions
            .Where(p => p.TrailingStopType == TrailingStopType.ATR)
            .Select(p => p.Symbol)
            .Distinct()
            .ToHashSet();

        var atrBySymbol = new Dictionary<string, decimal>();
        foreach (var sym in atrSymbols)
        {
            var candles = await readCtx.Set<Candle>()
                .Where(c => c.Symbol == sym && c.IsClosed && !c.IsDeleted)
                .OrderByDescending(c => c.Timestamp)
                .Take(15)           // 14-period ATR requires 14 closed candles + 1 prev
                .AsNoTracking()
                .ToListAsync(ct);

            atrBySymbol[sym] = ComputeAtr(candles);
        }

        int updated = 0;

        foreach (var pos in positions)
        {
            ct.ThrowIfCancellationRequested();

            var priceData = _priceCache.Get(pos.Symbol);
            if (priceData is null) continue;

            decimal current = (priceData.Value.Bid + priceData.Value.Ask) / 2m;

            // ── Calculate trailing distance in price units ────────────────────
            decimal trailDistance = pos.TrailingStopType switch
            {
                TrailingStopType.FixedPips  => pos.TrailingStopValue!.Value / 10_000m,
                TrailingStopType.Percentage => current * pos.TrailingStopValue!.Value / 100m,
                TrailingStopType.ATR        =>
                    atrBySymbol.TryGetValue(pos.Symbol, out var atr) && atr > 0
                        ? atr * pos.TrailingStopValue!.Value
                        : LogAtrFallback(pos),   // fallback to FixedPips when ATR unavailable
                _                           => pos.TrailingStopValue!.Value / 10_000m
            };

            if (trailDistance <= 0) continue;

            // ── Compute candidate new SL ──────────────────────────────────────
            decimal newSl;
            if (pos.Direction == PositionDirection.Long)
            {
                newSl = current - trailDistance;
                // One-way ratchet: never move SL below its current level
                if (pos.StopLoss.HasValue && newSl <= pos.StopLoss.Value)
                    continue;
            }
            else
            {
                newSl = current + trailDistance;
                // One-way ratchet: never move SL above its current level (short)
                if (pos.StopLoss.HasValue && newSl >= pos.StopLoss.Value)
                    continue;
            }

            // ── Bounds validation: SL must be positive and on the correct side of entry ──
            if (newSl <= 0)
            {
                _logger.LogWarning(
                    "TrailingStop: position {Id} ({Symbol}) computed invalid SL {NewSl:F5} — skipping",
                    pos.Id, pos.Symbol, newSl);
                continue;
            }

            if (pos.Direction == PositionDirection.Long && newSl >= current)
            {
                _logger.LogWarning(
                    "TrailingStop: position {Id} ({Symbol} Long) SL {NewSl:F5} >= current price {Price:F5} — skipping",
                    pos.Id, pos.Symbol, newSl, current);
                continue;
            }

            if (pos.Direction == PositionDirection.Short && newSl <= current)
            {
                _logger.LogWarning(
                    "TrailingStop: position {Id} ({Symbol} Short) SL {NewSl:F5} <= current price {Price:F5} — skipping",
                    pos.Id, pos.Symbol, newSl, current);
                continue;
            }

            // ── Persist the updated SL, trail reference price, and EACommand atomically ──
            // Load the tracked entity so we can batch the SL update and EACommand creation
            // into a single SaveChangesAsync call, ensuring atomicity.
            try
            {
                var trackedPos = await writeCtx.Set<Position>()
                    .FirstOrDefaultAsync(p => p.Id == pos.Id && !p.IsDeleted, ct);

                if (trackedPos is null || trackedPos.Status != PositionStatus.Open)
                {
                    _logger.LogWarning(
                        "TrailingStop: position {Id} ({Symbol}) not found or already closed — skipping.",
                        pos.Id, pos.Symbol);
                    continue;
                }

                trackedPos.StopLoss          = newSl;
                trackedPos.TrailingStopLevel  = current;

                // ── Queue EACommand so the EA updates the SL on MT5 ─────────────
                if (!string.IsNullOrEmpty(pos.BrokerPositionId))
                {
                    // Check for pending (unacknowledged) trailing stop command for this position
                    // before issuing a new one — prevents command queue flooding when ack is slow
                    var brokerTicket = long.TryParse(pos.BrokerPositionId, out var parsedTicket) ? parsedTicket : (long?)null;
                    bool hasPendingCommand = brokerTicket.HasValue && await writeCtx.Set<EACommand>()
                        .AnyAsync(c => c.TargetTicket == brokerTicket.Value
                                    && c.CommandType == EACommandType.UpdateTrailing
                                    && !c.Acknowledged
                                    && !c.IsDeleted, ct);

                    if (hasPendingCommand)
                    {
                        _logger.LogDebug(
                            "TrailingStop: skipping EA command for position {Id} ({Symbol}) — previous UpdateTrailing command still pending",
                            pos.Id, pos.Symbol);
                    }
                    else
                    {
                        var eaInstance = await writeCtx.Set<EAInstance>()
                            .ActiveForSymbol(pos.Symbol)
                            .FirstOrDefaultAsync(ct);

                        if (eaInstance is not null)
                        {
                            await writeCtx.Set<EACommand>().AddAsync(new EACommand
                            {
                                TargetInstanceId = eaInstance.InstanceId,
                                CommandType      = EACommandType.UpdateTrailing,
                                TargetTicket     = long.TryParse(pos.BrokerPositionId, out var ticket) ? ticket : null,
                                Symbol           = pos.Symbol,
                                Parameters       = JsonSerializer.Serialize(new { stopLoss = newSl }),
                            }, ct);
                        }
                    }
                }

                // Single SaveChangesAsync: SL update + EACommand creation are atomic
                await writeCtx.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Dead-letter the failed update to prevent silent loss of SL modifications
                _logger.LogError(ex,
                    "TrailingStop: FAILED to update SL and queue EA command for position {Id} ({Symbol}) SL→{NewSl:F5} — dead-lettering",
                    pos.Id, pos.Symbol, newSl);

                try
                {
                    using var dlScope = _scopeFactory.CreateScope();
                    var deadLetterSink = dlScope.ServiceProvider.GetRequiredService<IDeadLetterSink>();

                    await deadLetterSink.WriteAsync(
                        handlerName:      nameof(TrailingStopWorker),
                        eventType:        "EACommand:UpdateTrailing",
                        eventPayloadJson: JsonSerializer.Serialize(new { positionId = pos.Id, symbol = pos.Symbol, newStopLoss = newSl, brokerPositionId = pos.BrokerPositionId }),
                        errorMessage:     ex.Message,
                        stackTrace:       ex.StackTrace,
                        attempts:         1,
                        ct);
                }
                catch (Exception dlEx)
                {
                    _logger.LogCritical(dlEx,
                        "TrailingStop: FAILED to dead-letter EA command for position {Id} — command may be lost",
                        pos.Id);
                }
            }

            updated++;

            _logger.LogDebug(
                "TrailingStop: position {Id} ({Symbol} {Dir}) SL ratcheted {Old} → {New:F5} at price {Price:F5}",
                pos.Id, pos.Symbol, pos.Direction, pos.StopLoss?.ToString("F5") ?? "none", newSl, current);
        }

        if (updated > 0)
            _logger.LogInformation("TrailingStopWorker: ratcheted {Count} trailing stop(s).", updated);
    }

    // ── ATR helper ────────────────────────────────────────────────────────────

    /// <summary>
    /// 14-period Average True Range.
    /// Logs a warning when ATR trailing stop falls back to FixedPips due to insufficient candles,
    /// and returns the FixedPips fallback distance.
    private decimal LogAtrFallback(Domain.Entities.Position pos)
    {
        _logger.LogWarning(
            "TrailingStopWorker: ATR unavailable for {Symbol} (position {Id}) — insufficient candle data. " +
            "Falling back to FixedPips ({Value}). Consider ensuring candle history is available.",
            pos.Symbol, pos.Id, pos.TrailingStopValue);
        return pos.TrailingStopValue!.Value / 10_000m;
    }

    /// TR = max(High − Low, |High − PrevClose|, |Low − PrevClose|).
    /// <paramref name="candles"/> should be ordered descending by timestamp; the method
    /// takes up to 15 candles (14 TR values) and returns the simple average.
    /// </summary>
    private static decimal ComputeAtr(List<Candle> candles)
    {
        if (candles.Count < 2) return 0m;

        var ordered = candles.OrderByDescending(c => c.Timestamp).Take(15).ToList();
        var trs     = new List<decimal>(ordered.Count - 1);

        for (int i = 0; i < ordered.Count - 1; i++)
        {
            var c    = ordered[i];
            var prev = ordered[i + 1];

            decimal hl  = c.High - c.Low;
            decimal hpc = Math.Abs(c.High - prev.Close);
            decimal lpc = Math.Abs(c.Low  - prev.Close);
            trs.Add(Math.Max(hl, Math.Max(hpc, lpc)));
        }

        return trs.Count > 0 ? trs.Average() : 0m;
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
