using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Forward-test fill record for a signal produced by an approved-but-not-active strategy.
///
/// <para>
/// Closes the "no forward-test data in the loop" gap: approved strategies route their signals
/// to the paper simulator (not the real EA), which applies live TCA-derived slippage/spread
/// /commission, opens a simulated position, and closes it on SL/TP/Timeout using tick data
/// already flowing from the EA. The promotion gate reads from this table instead of using
/// backtest-trade count as a proxy, so activation requires real live-data performance.
/// </para>
/// </summary>
public class PaperExecution : Entity<long>
{
    public long StrategyId { get; set; }
    public virtual Strategy? Strategy { get; set; }

    /// <summary>Optional link back to the originating TradeSignal when one was persisted.</summary>
    public long? TradeSignalId { get; set; }
    public virtual TradeSignal? TradeSignal { get; set; }

    public string    Symbol    { get; set; } = string.Empty;
    public Timeframe Timeframe { get; set; }
    public TradeDirection Direction { get; set; }

    public DateTime SignalGeneratedAt { get; set; }

    public decimal  RequestedEntryPrice { get; set; }
    public decimal  SimulatedFillPrice  { get; set; }
    public DateTime SimulatedFillAt     { get; set; }

    public decimal SimulatedSlippagePriceUnits  { get; set; }
    public decimal SimulatedSpreadCostPriceUnits { get; set; }
    public decimal SimulatedCommissionAccountCcy { get; set; }

    public decimal LotSize      { get; set; }
    public decimal ContractSize { get; set; }
    public decimal PipSize      { get; set; }

    public decimal? StopLoss    { get; set; }
    public decimal? TakeProfit  { get; set; }

    public DateTime?        ClosedAt               { get; set; }
    public decimal?         SimulatedExitPrice     { get; set; }
    public PaperExitReason? SimulatedExitReason    { get; set; }
    public decimal?         SimulatedGrossPnL      { get; set; }
    public decimal?         SimulatedNetPnL        { get; set; }
    /// <summary>Maximum adverse excursion in price units while open.</summary>
    public decimal?         SimulatedMaeAbsolute   { get; set; }
    /// <summary>Maximum favourable excursion in price units while open.</summary>
    public decimal?         SimulatedMfeAbsolute   { get; set; }

    /// <summary>JSON snapshot of the TCA profile used at open — audit trail.</summary>
    public string? TcaProfileSnapshotJson { get; set; }

    public PaperExecutionStatus Status { get; set; } = PaperExecutionStatus.Open;

    /// <summary>
    /// True when this row was backfilled by replaying historical candles through
    /// BacktestEngine instead of coming from live tick data. Gate 4 of the promotion
    /// validator excludes synthetic rows from the hard minimum-count — they are for
    /// observability / operator backfill, not forward-test evidence.
    /// </summary>
    public bool IsSynthetic { get; set; }

    public bool IsDeleted  { get; set; }
    public uint RowVersion { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
