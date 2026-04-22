using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Builds resolved signal-level A/B test state from closed positions and served-model attribution.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(ISignalAbTestStateBuilder))]
public sealed class SignalAbTestStateBuilder : ISignalAbTestStateBuilder
{
    public async Task<AbTestState> BuildAsync(
        DbContext readDb,
        long championId,
        long challengerId,
        string symbol,
        Timeframe timeframe,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        var closedPositions = await readDb.Set<Position>()
            .AsNoTracking()
            .Where(p => p.Symbol == symbol &&
                        p.Status == PositionStatus.Closed &&
                        !p.IsDeleted &&
                        p.ClosedAt != null &&
                        p.ClosedAt >= startedAt &&
                        p.OpenedAt >= startedAt &&
                        p.OpenOrderId != null)
            .Select(p => new
            {
                p.RealizedPnL,
                p.OpenedAt,
                p.ClosedAt,
                p.OpenOrderId,
            })
            .ToListAsync(cancellationToken);

        if (closedPositions.Count == 0)
            return EmptyState(championId, challengerId, symbol, timeframe, startedAt);

        var openOrderIds = closedPositions
            .Where(p => p.OpenOrderId.HasValue)
            .Select(p => p.OpenOrderId!.Value)
            .Distinct()
            .ToList();

        var orderSignalMap = await readDb.Set<Order>()
            .AsNoTracking()
            .Where(o => openOrderIds.Contains(o.Id) && o.TradeSignalId != null)
            .Select(o => new { o.Id, o.TradeSignalId, o.StrategyId })
            .ToDictionaryAsync(
                o => o.Id,
                o => new OrderSignalAttribution(o.TradeSignalId!.Value, o.StrategyId),
                cancellationToken);

        var signalIds = orderSignalMap.Values.Select(x => x.TradeSignalId).Distinct().ToList();
        if (signalIds.Count == 0)
            return EmptyState(championId, challengerId, symbol, timeframe, startedAt);

        var signalModelMap = await readDb.Set<TradeSignal>()
            .AsNoTracking()
            .Where(s => signalIds.Contains(s.Id) && s.MLModelId != null)
            .Select(s => new { s.Id, s.MLModelId })
            .ToDictionaryAsync(s => s.Id, s => s.MLModelId!.Value, cancellationToken);

        var predictionLogs = await readDb.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(pl => signalIds.Contains(pl.TradeSignalId) &&
                         (pl.MLModelId == championId || pl.MLModelId == challengerId) &&
                         pl.Timeframe == timeframe &&
                         pl.PredictedAt >= startedAt &&
                         !pl.IsDeleted)
            .Select(pl => new { pl.TradeSignalId, pl.MLModelId, pl.PredictedAt })
            .ToListAsync(cancellationToken);

        var predictionLogsBySignal = predictionLogs
            .GroupBy(pl => pl.TradeSignalId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(pl => pl.PredictedAt).ToList());

        var champOutcomes = new List<AbTestOutcome>();
        var challOutcomes = new List<AbTestOutcome>();

        foreach (var pos in closedPositions)
        {
            if (!pos.OpenOrderId.HasValue) continue;
            if (!orderSignalMap.TryGetValue(pos.OpenOrderId.Value, out var attribution)) continue;
            var signalId = attribution.TradeSignalId;

            long? modelId = signalModelMap.TryGetValue(signalId, out var sigModelId) &&
                            (sigModelId == championId || sigModelId == challengerId)
                ? sigModelId
                : predictionLogsBySignal.TryGetValue(signalId, out var logs)
                    ? logs.FirstOrDefault()?.MLModelId
                    : null;

            if (modelId is null) continue;

            var durationMinutes = pos.ClosedAt.HasValue
                ? (int)(pos.ClosedAt.Value - pos.OpenedAt).TotalMinutes
                : 0;

            var outcome = new AbTestOutcome(
                Pnl:             (double)pos.RealizedPnL,
                Magnitude:       Math.Abs((double)pos.RealizedPnL),
                DurationMinutes: durationMinutes,
                ResolvedAtUtc:   pos.ClosedAt ?? DateTime.UtcNow,
                StrategyId:      attribution.StrategyId,
                SessionHourUtc:  pos.OpenedAt.Hour);

            if (modelId == championId)
                champOutcomes.Add(outcome);
            else if (modelId == challengerId)
                challOutcomes.Add(outcome);
        }

        return new AbTestState(0, championId, challengerId, symbol, timeframe, startedAt,
            champOutcomes, challOutcomes);
    }

    private static AbTestState EmptyState(
        long championId,
        long challengerId,
        string symbol,
        Timeframe timeframe,
        DateTime startedAt)
        => new(0, championId, challengerId, symbol, timeframe, startedAt, [], []);

    private sealed record OrderSignalAttribution(long TradeSignalId, long StrategyId);
}
