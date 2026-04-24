using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Performance attribution snapshot for a trading account, decomposing returns
/// into alpha sources. Used for both end-of-day reporting and intraday running
/// snapshots.
/// </summary>
public class AccountPerformanceAttribution : Entity<long>
{
    /// <summary>FK to the trading account.</summary>
    public long TradingAccountId { get; set; }

    /// <summary>Date this attribution covers.</summary>
    public DateTime AttributionDate { get; set; }

    /// <summary>
    /// Indicates whether this row is a daily end-of-day snapshot or an hourly
    /// intraday running snapshot.
    /// </summary>
    public PerformanceAttributionGranularity Granularity { get; set; } = PerformanceAttributionGranularity.Daily;

    /// <summary>Account equity at start of day.</summary>
    public decimal StartOfDayEquity { get; set; }

    /// <summary>Account equity at end of day.</summary>
    public decimal EndOfDayEquity { get; set; }

    /// <summary>Total realized P&amp;L for the day.</summary>
    public decimal RealizedPnl { get; set; }

    /// <summary>Total unrealized P&amp;L change for the day.</summary>
    public decimal UnrealizedPnlChange { get; set; }

    /// <summary>Total return percentage for the day.</summary>
    public decimal DailyReturnPct { get; set; }

    /// <summary>
    /// JSON breakdown of P&amp;L by strategy:
    /// [{ "strategyId": 1, "strategyName": "MA Crossover", "pnl": 150.00, "trades": 3 }]
    /// </summary>
    public string StrategyAttributionJson { get; set; } = "[]";

    /// <summary>
    /// JSON breakdown of P&amp;L by symbol:
    /// [{ "symbol": "EURUSD", "pnl": 200.00, "trades": 5 }]
    /// </summary>
    public string SymbolAttributionJson { get; set; } = "[]";

    /// <summary>Incremental P&amp;L from ML-scored signals vs. rule-only signals.</summary>
    public decimal MLAlphaPnl { get; set; }

    /// <summary>P&amp;L from entry/exit timing vs. signal price.</summary>
    public decimal TimingAlphaPnl { get; set; }

    /// <summary>Total execution costs incurred (spread + slippage + commission).</summary>
    public decimal ExecutionCosts { get; set; }

    /// <summary>Rolling 7-day Sharpe ratio.</summary>
    public decimal SharpeRatio7d { get; set; }

    /// <summary>Rolling 30-day Sharpe ratio.</summary>
    public decimal SharpeRatio30d { get; set; }

    /// <summary>Rolling 30-day Sortino ratio.</summary>
    public decimal SortinoRatio30d { get; set; }

    /// <summary>Rolling 30-day Calmar ratio.</summary>
    public decimal CalmarRatio30d { get; set; }

    /// <summary>Return of buy-and-hold benchmark on the same symbols for comparison.</summary>
    public decimal BenchmarkReturnPct { get; set; }

    /// <summary>Alpha vs benchmark (DailyReturnPct - BenchmarkReturnPct).</summary>
    public decimal AlphaVsBenchmarkPct { get; set; }

    /// <summary>Active return: DailyReturnPct minus BenchmarkReturnPct.</summary>
    public decimal ActiveReturnPct { get; set; }

    /// <summary>Information ratio: rolling alpha / tracking error (30 days).</summary>
    public decimal InformationRatio { get; set; }

    /// <summary>Gross alpha before execution costs.</summary>
    public decimal GrossAlphaPct { get; set; }

    /// <summary>Execution costs as percentage of start-of-day equity.</summary>
    public decimal ExecutionCostPct { get; set; }

    /// <summary>Net alpha after subtracting execution costs.</summary>
    public decimal NetAlphaPct { get; set; }

    /// <summary>Number of trades executed this day.</summary>
    public int TradeCount { get; set; }

    /// <summary>Win rate for trades closed this day.</summary>
    public decimal WinRate { get; set; }

    public virtual TradingAccount TradingAccount { get; set; } = null!;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
