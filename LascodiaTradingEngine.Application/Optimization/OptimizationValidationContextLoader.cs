using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class OptimizationValidationContextLoader
{
    private readonly OptimizationGridBuilder _gridBuilder;

    public OptimizationValidationContextLoader(OptimizationGridBuilder gridBuilder)
    {
        _gridBuilder = gridBuilder;
    }

    internal async Task<OptimizationValidationContext> LoadAsync(
        DbContext db,
        Strategy strategy,
        OptimizationRun run,
        IReadOnlyList<Candle> trainCandles,
        IReadOnlyList<Candle> testCandles,
        int candidateCount,
        int maxRunTimeoutMinutes,
        CancellationToken ct)
    {
        var parameterGrid = await _gridBuilder.BuildParameterGridAsync(db, strategy.StrategyType, ct);
        var parameterBounds = OptimizationGridBuilder.ExtractTpeBounds(parameterGrid);

        var higherTf = OptimizationPolicyHelpers.GetHigherTimeframe(strategy.Timeframe);
        MarketRegimeEnum? higherRegime = null;
        if (higherTf.HasValue)
        {
            higherRegime = await db.Set<MarketRegimeSnapshot>()
                .Where(s => s.Symbol == strategy.Symbol && s.Timeframe == higherTf.Value && !s.IsDeleted)
                .OrderByDescending(s => s.DetectedAt)
                .Select(s => (MarketRegimeEnum?)s.Regime)
                .FirstOrDefaultAsync(ct);
        }

        var otherActiveParamsJson = await db.Set<Strategy>()
            .Where(s => s.Id != strategy.Id
                     && s.Status == StrategyStatus.Active
                     && s.StrategyType == strategy.StrategyType
                     && s.Symbol == strategy.Symbol
                     && !s.IsDeleted)
            .Select(s => s.ParametersJson)
            .ToListAsync(ct);

        var otherActiveParsed = new List<Dictionary<string, JsonElement>>();
        foreach (var json in otherActiveParamsJson)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (parsed is not null && parsed.Count > 0)
                    otherActiveParsed.Add(parsed);
            }
            catch (JsonException)
            {
                // Ignore malformed historical parameters when evaluating the current candidate set.
            }
        }

        MarketRegimeEnum? testWindowRegime = null;
        if (testCandles.Count > 0)
        {
            var testStart = testCandles[0].Timestamp;
            testWindowRegime = await db.Set<MarketRegimeSnapshot>()
                .Where(s => s.Symbol == strategy.Symbol
                         && s.Timeframe == strategy.Timeframe
                         && !s.IsDeleted
                         && s.DetectedAt <= testStart)
                .OrderByDescending(s => s.DetectedAt)
                .Select(s => (MarketRegimeEnum?)s.Regime)
                .FirstOrDefaultAsync(ct);
        }

        MarketRegimeEnum? trainWindowRegime = null;
        if (trainCandles.Count > 0)
        {
            var lastTrainTimestamp = trainCandles[^1].Timestamp;
            trainWindowRegime = await db.Set<MarketRegimeSnapshot>()
                .Where(s => s.Symbol == strategy.Symbol
                         && s.Timeframe == strategy.Timeframe
                         && !s.IsDeleted
                         && s.DetectedAt <= lastTrainTimestamp)
                .OrderByDescending(s => s.DetectedAt)
                .Select(s => (MarketRegimeEnum?)s.Regime)
                .FirstOrDefaultAsync(ct);
        }

        bool relaxDegradationThreshold =
            testWindowRegime.HasValue
            && trainWindowRegime.HasValue
            && testWindowRegime.Value != trainWindowRegime.Value;

        int gateTimeoutSeconds = Math.Max(60, maxRunTimeoutMinutes * 60 / Math.Max(1, candidateCount * 2));

        return new OptimizationValidationContext(
            parameterBounds,
            higherRegime,
            otherActiveParsed,
            testWindowRegime,
            trainWindowRegime,
            relaxDegradationThreshold,
            gateTimeoutSeconds);
    }
}

internal sealed record OptimizationValidationContext(
    Dictionary<string, (double Min, double Max, bool IsInteger)> ParameterBounds,
    MarketRegimeEnum? HigherRegime,
    List<Dictionary<string, JsonElement>> OtherActiveParsed,
    MarketRegimeEnum? TestWindowRegime,
    MarketRegimeEnum? TrainWindowRegime,
    bool RelaxDegradationThreshold,
    int GateTimeoutSeconds);

