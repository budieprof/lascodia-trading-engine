using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published at the end of each StrategyGenerationWorker cycle with a summary of what happened.
/// Enables external dashboards and alerting without log parsing.
/// </summary>
public record StrategyGenerationCycleCompletedIntegrationEvent : IntegrationEvent
{
    public long SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>Total symbols evaluated (had fresh regime data and sufficient candles).</summary>
    public int SymbolsProcessed { get; init; }

    /// <summary>Total candidates that passed screening and were persisted.</summary>
    public int CandidatesCreated { get; init; }

    /// <summary>Of which were strategic reserve (counter-regime) candidates.</summary>
    public int ReserveCandidatesCreated { get; init; }

    /// <summary>Total candidates screened (IS backtest executed).</summary>
    public int CandidatesScreened { get; init; }

    /// <summary>Stale Draft strategies pruned in this cycle.</summary>
    public int StrategiesPruned { get; init; }

    /// <summary>Candidates removed by the portfolio drawdown filter.</summary>
    public int PortfolioFilterRemoved { get; init; }

    /// <summary>Symbols skipped (and why) during the cycle.</summary>
    public int SymbolsSkipped { get; init; }

    /// <summary>Cycle wall-clock duration in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Whether the circuit breaker is currently tripped.</summary>
    public bool CircuitBreakerActive { get; init; }

    /// <summary>Consecutive failure count at cycle end.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>True when the cycle exited early without attempting generation work.</summary>
    public bool Skipped { get; init; }

    /// <summary>Short machine-readable reason when <see cref="Skipped"/> is true.</summary>
    public string? SkipReason { get; init; }

    /// <summary>UTC timestamp when the cycle completed.</summary>
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
}
