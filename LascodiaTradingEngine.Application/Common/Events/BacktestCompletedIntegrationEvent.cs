using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>BacktestWorker</c> when a backtest run completes successfully.
/// The primary consumer automatically queues a <c>WalkForwardRun</c> using the same
/// symbol/timeframe/date-window, advancing the standard validation pipeline:
/// Backtest → Walk-Forward → (optional) Promotion.
/// </summary>
public record BacktestCompletedIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long      SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>The completed BacktestRun's database Id.</summary>
    public long      BacktestRunId { get; init; }

    /// <summary>The strategy that was backtested.</summary>
    public long      StrategyId    { get; init; }

    /// <summary>Currency pair used in the backtest.</summary>
    public string    Symbol        { get; init; } = string.Empty;

    /// <summary>Timeframe used in the backtest.</summary>
    public Timeframe Timeframe     { get; init; }

    /// <summary>Start of the historical data window.</summary>
    public DateTime  FromDate      { get; init; }

    /// <summary>End of the historical data window.</summary>
    public DateTime  ToDate        { get; init; }

    /// <summary>Initial account balance used during the replay.</summary>
    public decimal   InitialBalance { get; init; }

    /// <summary>UTC timestamp when the backtest completed.</summary>
    public DateTime  CompletedAt   { get; init; }
}
