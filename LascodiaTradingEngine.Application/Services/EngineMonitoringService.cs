using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Real-time engine health and performance metrics, consumable by dashboards and alerting systems.
/// Aggregates across all subsystems: ML models, positions, EA instance health, training queue, and drift.
/// </summary>
public interface IEngineMonitoringService
{
    /// <summary>Produces a point-in-time snapshot of all engine metrics.</summary>
    Task<EngineHealthSnapshot> GetSnapshotAsync(CancellationToken ct);
}

/// <summary>Point-in-time snapshot of all engine health metrics.</summary>
public sealed record EngineHealthSnapshot
{
    // ── ML Model Health ─────────────────────────────────────────────────────
    public int    ActiveModels           { get; init; }
    public int    ModelsInDrift          { get; init; }
    public double AvgLiveAccuracy        { get; init; }
    public double AvgLiveSharpe          { get; init; }

    // ── Training Pipeline ───────────────────────────────────────────────────
    public int    QueuedTrainingRuns     { get; init; }
    public int    RunningTrainingRuns    { get; init; }
    public int    CompletedLast24h       { get; init; }
    public int    FailedLast24h          { get; init; }

    // ── Portfolio ───────────────────────────────────────────────────────────
    public int    OpenPositions          { get; init; }
    public decimal TotalExposureLots     { get; init; }
    public decimal UnrealizedPnl         { get; init; }
    public decimal AccountEquity         { get; init; }
    public double DrawdownPct            { get; init; }

    // ── EA Instances ──────────────────────────────────────────────────────
    public int    ActiveEAInstances      { get; init; }
    public int    DisconnectedEAInstances { get; init; }
    public bool   EAHealthy              { get; init; }
    public double AvgSlippagePips        { get; init; }
    public double P95InferenceLatencyMs  { get; init; }

    // ── Signals ─────────────────────────────────────────────────────────────
    public int    SignalsLast24h         { get; init; }
    public int    SignalsApproved        { get; init; }
    public int    SignalsRejected        { get; init; }

    // ── Feature Drift ───────────────────────────────────────────────────────
    public int    FeaturesWithPsiAboveThreshold { get; init; }

    // ── Timestamp ───────────────────────────────────────────────────────────
    public DateTime SnapshotAt           { get; init; }
}

[RegisterService]
public sealed class EngineMonitoringService : IEngineMonitoringService
{
    private static readonly TimeSpan HeartbeatThreshold = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EngineMonitoringService> _logger;

    public EngineMonitoringService(
        IServiceScopeFactory scopeFactory,
        ILogger<EngineMonitoringService> logger)
    {
        _scopeFactory   = scopeFactory;
        _logger         = logger;
    }

    public async Task<EngineHealthSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readDb      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var ctx         = readDb.GetDbContext();

        var now   = DateTime.UtcNow;
        var day   = now.AddHours(-24);

        // ── ML Models ───────────────────────────────────────────────────────
        var activeModels = await ctx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        int modelsInDrift = 0;
        double accSum = 0, sharpeSum = 0;
        foreach (var m in activeModels)
        {
            if (m.LiveDirectionAccuracy.HasValue)
                accSum += (double)m.LiveDirectionAccuracy.Value;
            if (m.SharpeRatio.HasValue)
                sharpeSum += (double)m.SharpeRatio.Value;
            if (m.DirectionAccuracy.HasValue && m.LiveDirectionAccuracy.HasValue &&
                (double)m.LiveDirectionAccuracy.Value < (double)m.DirectionAccuracy.Value * 0.85)
                modelsInDrift++;
        }

        // ── Training Pipeline ───────────────────────────────────────────────
        int queued   = await ctx.Set<MLTrainingRun>().CountAsync(r => r.Status == RunStatus.Queued && !r.IsDeleted, ct);
        int running  = await ctx.Set<MLTrainingRun>().CountAsync(r => r.Status == RunStatus.Running && !r.IsDeleted, ct);
        int completed = await ctx.Set<MLTrainingRun>().CountAsync(r => r.Status == RunStatus.Completed && r.CompletedAt >= day && !r.IsDeleted, ct);
        int failed   = await ctx.Set<MLTrainingRun>().CountAsync(r => r.Status == RunStatus.Failed && r.CompletedAt >= day && !r.IsDeleted, ct);

        // ── Portfolio ───────────────────────────────────────────────────────
        var openPositions = await ctx.Set<Position>()
            .Where(p => p.Status == PositionStatus.Open && !p.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        decimal totalLots = openPositions.Sum(p => p.OpenLots);
        decimal unrealizedPnl = openPositions.Sum(p => p.UnrealizedPnL);

        var account = await ctx.Set<TradingAccount>()
            .Where(a => a.IsActive && !a.IsDeleted)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        decimal equity = account?.Equity ?? 0;
        double drawdownPct = 0;
        if (account is not null && account.Balance > 0)
            drawdownPct = (double)(1m - equity / account.Balance) * 100;

        // ── EA Health ─────────────────────────────────────────────────────
        var heartbeatCutoff = now - HeartbeatThreshold;
        var activeInstances = await ctx.Set<EAInstance>()
            .Where(e => e.Status == EAInstanceStatus.Active && !e.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);
        bool eaHealthy = activeInstances.Count > 0 && activeInstances.All(e => e.LastHeartbeat >= heartbeatCutoff);
        int disconnectedCount = await ctx.Set<EAInstance>()
            .CountAsync(e => e.Status == EAInstanceStatus.Disconnected && !e.IsDeleted, ct);

        var recentFills = await ctx.Set<ExecutionQualityLog>()
            .Where(e => e.RecordedAt >= day && !e.IsDeleted)
            .AsNoTracking()
            .Select(e => new { e.SlippagePips })
            .ToListAsync(ct);
        double avgSlippage = recentFills.Count > 0 ? recentFills.Average(e => (double)e.SlippagePips) : 0;

        // Inference latency P95
        var recentLatencies = await ctx.Set<MLModelPredictionLog>()
            .Where(l => l.PredictedAt >= day && l.LatencyMs.HasValue && !l.IsDeleted)
            .Select(l => l.LatencyMs!.Value)
            .ToListAsync(ct);
        double p95Latency = 0;
        if (recentLatencies.Count > 0)
        {
            recentLatencies.Sort();
            int p95Idx = (int)(recentLatencies.Count * 0.95);
            p95Latency = recentLatencies[Math.Min(p95Idx, recentLatencies.Count - 1)];
        }

        // ── Signals ─────────────────────────────────────────────────────────
        int signalsTotal = await ctx.Set<TradeSignal>()
            .CountAsync(s => s.GeneratedAt >= day && !s.IsDeleted, ct);
        int signalsApproved = await ctx.Set<TradeSignal>()
            .CountAsync(s => s.GeneratedAt >= day && s.Status == TradeSignalStatus.Approved && !s.IsDeleted, ct);
        int signalsRejected = await ctx.Set<TradeSignal>()
            .CountAsync(s => s.GeneratedAt >= day && s.Status == TradeSignalStatus.Rejected && !s.IsDeleted, ct);

        var snapshot = new EngineHealthSnapshot
        {
            ActiveModels           = activeModels.Count,
            ModelsInDrift          = modelsInDrift,
            AvgLiveAccuracy        = activeModels.Count > 0 ? accSum / activeModels.Count : 0,
            AvgLiveSharpe          = activeModels.Count > 0 ? sharpeSum / activeModels.Count : 0,
            QueuedTrainingRuns     = queued,
            RunningTrainingRuns    = running,
            CompletedLast24h       = completed,
            FailedLast24h          = failed,
            OpenPositions          = openPositions.Count,
            TotalExposureLots      = totalLots,
            UnrealizedPnl          = unrealizedPnl,
            AccountEquity          = equity,
            DrawdownPct            = drawdownPct,
            ActiveEAInstances      = activeInstances.Count,
            DisconnectedEAInstances = disconnectedCount,
            EAHealthy              = eaHealthy,
            AvgSlippagePips        = avgSlippage,
            P95InferenceLatencyMs  = p95Latency,
            SignalsLast24h         = signalsTotal,
            SignalsApproved        = signalsApproved,
            SignalsRejected        = signalsRejected,
            SnapshotAt             = now,
        };

        _logger.LogDebug(
            "EngineMonitoring: models={Models} drift={Drift} positions={Pos} equity={Eq:F0} drawdown={DD:F1}% ea={EA}",
            snapshot.ActiveModels, snapshot.ModelsInDrift, snapshot.OpenPositions,
            snapshot.AccountEquity, snapshot.DrawdownPct, snapshot.ActiveEAInstances);

        return snapshot;
    }
}
