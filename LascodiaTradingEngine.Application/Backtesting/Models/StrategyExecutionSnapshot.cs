using System.Text.Json;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting.Models;

public sealed record StrategyExecutionSnapshot
{
    public long StrategyId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public StrategyType StrategyType { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public Timeframe Timeframe { get; init; }
    public string? ParametersJson { get; init; }
    public StrategyStatus Status { get; init; }
    public long? RiskProfileId { get; init; }
    public DateTime CreatedAt { get; init; }
    public StrategyLifecycleStage LifecycleStage { get; init; }
    public DateTime? LifecycleStageEnteredAt { get; init; }
    public decimal? EstimatedCapacityLots { get; init; }

    public Strategy ToStrategy() => new()
    {
        Id = StrategyId,
        Name = Name,
        Description = Description ?? string.Empty,
        StrategyType = StrategyType,
        Symbol = Symbol,
        Timeframe = Timeframe,
        ParametersJson = ParametersJson ?? "{}",
        Status = Status,
        RiskProfileId = RiskProfileId,
        CreatedAt = CreatedAt,
        LifecycleStage = LifecycleStage,
        LifecycleStageEnteredAt = LifecycleStageEnteredAt,
        EstimatedCapacityLots = EstimatedCapacityLots,
        IsDeleted = false
    };

    public static StrategyExecutionSnapshot FromStrategy(Strategy strategy, string? parametersJsonOverride = null) => new()
    {
        StrategyId = strategy.Id,
        Name = strategy.Name ?? string.Empty,
        Description = strategy.Description,
        StrategyType = strategy.StrategyType,
        Symbol = strategy.Symbol ?? string.Empty,
        Timeframe = strategy.Timeframe,
        ParametersJson = parametersJsonOverride ?? strategy.ParametersJson,
        Status = strategy.Status,
        RiskProfileId = strategy.RiskProfileId,
        CreatedAt = strategy.CreatedAt,
        LifecycleStage = strategy.LifecycleStage,
        LifecycleStageEnteredAt = strategy.LifecycleStageEnteredAt,
        EstimatedCapacityLots = strategy.EstimatedCapacityLots
    };

    public static StrategyExecutionSnapshot? Deserialize(string? strategySnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(strategySnapshotJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<StrategyExecutionSnapshot>(strategySnapshotJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
