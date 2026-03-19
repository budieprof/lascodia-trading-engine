using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Tracks the signal lifecycle funnel for ML-tagged trade signals:
/// <c>Pending → Approved → Executed</c> (and separately, the fraction that are
/// <c>Rejected</c> or <c>Expired</c> before execution).
///
/// <b>Problem:</b> A model's backtested performance assumes immediate execution
/// at the signal price. In production, signals that are approved by risk logic
/// but subsequently rejected or expire unfilled cause the live results to diverge
/// from backtested expectations — and this divergence is invisible to accuracy
/// or P&amp;L workers that only look at <i>executed</i> signals.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>For each symbol, count ML-tagged signals (those with a non-null
///         <see cref="TradeSignal.MLModelId"/>) in the look-back window,
///         grouped by final <see cref="TradeSignalStatus"/>.</item>
///   <item>Compute fill rate = <c>Executed / Created</c> and rejection rate
///         = <c>(Rejected + Expired) / Created</c>.</item>
///   <item>Write <c>MLFunnel:{Symbol}:FillRate</c> and
///         <c>MLFunnel:{Symbol}:RejectionRate</c> to <see cref="EngineConfig"/>.</item>
///   <item>Alert when fill rate &lt; <c>FillRateFloor</c> (default 0.50) or
///         rejection rate &gt; <c>RejectionRateCeiling</c> (default 0.40).</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLFunnel:PollIntervalSeconds</c>      — default 3600 (1 h)</item>
///   <item><c>MLFunnel:WindowDays</c>               — look-back, default 7</item>
///   <item><c>MLFunnel:MinSignals</c>               — minimum ML signals in window, default 10</item>
///   <item><c>MLFunnel:FillRateFloor</c>            — fill rate alert, default 0.50</item>
///   <item><c>MLFunnel:RejectionRateCeiling</c>     — rejection rate alert, default 0.40</item>
///   <item><c>MLFunnel:AlertDestination</c>         — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLSignalFunnelWorker : BackgroundService
{
    private const string CK_PollSecs    = "MLFunnel:PollIntervalSeconds";
    private const string CK_Window      = "MLFunnel:WindowDays";
    private const string CK_MinSig      = "MLFunnel:MinSignals";
    private const string CK_FillFloor   = "MLFunnel:FillRateFloor";
    private const string CK_RejCeiling  = "MLFunnel:RejectionRateCeiling";
    private const string CK_AlertDest   = "MLFunnel:AlertDestination";

    private readonly IServiceScopeFactory             _scopeFactory;
    private readonly ILogger<MLSignalFunnelWorker>    _logger;

    public MLSignalFunnelWorker(
        IServiceScopeFactory              scopeFactory,
        ILogger<MLSignalFunnelWorker>     logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSignalFunnelWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await CheckFunnelAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLSignalFunnelWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSignalFunnelWorker stopping.");
    }

    // ── Funnel check core ─────────────────────────────────────────────────────

    private async Task CheckFunnelAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays    = await GetConfigAsync<int>   (readCtx, CK_Window,     7,       ct);
        int    minSignals    = await GetConfigAsync<int>   (readCtx, CK_MinSig,     10,      ct);
        double fillFloor     = await GetConfigAsync<double>(readCtx, CK_FillFloor,  0.50,    ct);
        double rejCeiling    = await GetConfigAsync<double>(readCtx, CK_RejCeiling, 0.40,    ct);
        string alertDest     = await GetConfigAsync<string>(readCtx, CK_AlertDest,  "ml-ops", ct);

        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        // Load all ML-tagged signals in the window, grouped by symbol + status
        var signals = await readCtx.Set<TradeSignal>()
            .Where(s => s.MLModelId   != null          &&
                        s.GeneratedAt >= windowStart    &&
                        !s.IsDeleted)
            .AsNoTracking()
            .Select(s => new { s.Symbol, s.Status })
            .ToListAsync(ct);

        if (signals.Count == 0) return;

        var bySymbol = signals.GroupBy(s => s.Symbol);

        foreach (var group in bySymbol)
        {
            ct.ThrowIfCancellationRequested();

            string symbol = group.Key;
            var    list   = group.ToList();

            if (list.Count < minSignals) continue;

            try
            {
                await CheckSymbolFunnelAsync(
                    symbol, list.Select(s => s.Status).ToList(),
                    fillFloor, rejCeiling, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SignalFunnel: check failed for {Symbol} — skipping.", symbol);
            }
        }
    }

    private async Task CheckSymbolFunnelAsync(
        string                                  symbol,
        List<TradeSignalStatus>                 statuses,
        double                                  fillRateFloor,
        double                                  rejectionRateCeiling,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int total    = statuses.Count;
        int executed = statuses.Count(s => s == TradeSignalStatus.Executed);
        int rejected = statuses.Count(s => s == TradeSignalStatus.Rejected);
        int expired  = statuses.Count(s => s == TradeSignalStatus.Expired);
        int approved = statuses.Count(s => s == TradeSignalStatus.Approved);
        int pending  = statuses.Count(s => s == TradeSignalStatus.Pending);

        double fillRate      = (double)executed / total;
        double rejectionRate = (double)(rejected + expired) / total;

        _logger.LogDebug(
            "SignalFunnel: {Symbol} — total={T} executed={E} approved={A} rejected={R} expired={X} pending={P} " +
            "fillRate={FR:P1} rejRate={RR:P1}",
            symbol, total, executed, approved, rejected, expired, pending, fillRate, rejectionRate);

        // Write observability keys to EngineConfig
        await UpsertConfigAsync(writeCtx, $"MLFunnel:{symbol}:FillRate",      fillRate.ToString("F4"),      ct);
        await UpsertConfigAsync(writeCtx, $"MLFunnel:{symbol}:RejectionRate", rejectionRate.ToString("F4"), ct);

        bool fillAlert = fillRate < fillRateFloor;
        bool rejAlert  = rejectionRate > rejectionRateCeiling;

        if (!fillAlert && !rejAlert) return;

        string reason = fillAlert ? "signal_fill_rate_low" : "signal_rejection_rate_high";

        _logger.LogWarning(
            "SignalFunnel: {Symbol} — {Reason}: fillRate={FR:P1} (floor {FF:P0}) " +
            "rejRate={RR:P1} (ceiling {RC:P0})",
            symbol, reason, fillRate, fillRateFloor, rejectionRate, rejectionRateCeiling);

        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                  &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = symbol,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason,
                severity             = "warning",
                symbol,
                totalSignals         = total,
                executedSignals      = executed,
                approvedSignals      = approved,
                rejectedSignals      = rejected,
                expiredSignals       = expired,
                fillRate,
                fillRateFloor,
                rejectionRate,
                rejectionRateCeiling,
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── EngineConfig upsert ───────────────────────────────────────────────────

    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Value,         value)
                .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow),
                ct);

        if (rows == 0)
        {
            writeCtx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key             = key,
                Value           = value,
                DataType        = ConfigDataType.String,
                Description     = "ML signal funnel metric. Written by MLSignalFunnelWorker.",
                IsHotReloadable = true,
                LastUpdatedAt   = DateTime.UtcNow,
            });
            await writeCtx.SaveChangesAsync(ct);
        }
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
