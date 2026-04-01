using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Decomposes execution cost for a filled order into its constituent parts using
/// implementation-shortfall methodology. Computed by the TransactionCostWorker
/// after each fill and aggregated for TCA reporting.
/// </summary>
public class TransactionCostAnalysis : Entity<long>
{
    /// <summary>FK to the filled order.</summary>
    public long OrderId { get; set; }

    /// <summary>FK to the originating trade signal.</summary>
    public long? TradeSignalId { get; set; }

    /// <summary>Symbol for this order.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Mid-price at signal generation time (arrival benchmark).</summary>
    public decimal ArrivalPrice { get; set; }

    /// <summary>Price at which the order was actually filled.</summary>
    public decimal FillPrice { get; set; }

    /// <summary>Mid-price at order submission time.</summary>
    public decimal SubmissionPrice { get; set; }

    /// <summary>VWAP of the symbol during the signal's lifetime window.</summary>
    public decimal? VwapBenchmark { get; set; }

    /// <summary>
    /// Implementation shortfall: total cost of execution vs. decision price.
    /// Positive = execution cost; negative = execution gained.
    /// </summary>
    public decimal ImplementationShortfall { get; set; }

    /// <summary>Price drift from signal creation to EA poll (arrival → submission).</summary>
    public decimal DelayCost { get; set; }

    /// <summary>Price drift from order submission to fill (submission → fill).</summary>
    public decimal MarketImpactCost { get; set; }

    /// <summary>Half-spread at time of execution.</summary>
    public decimal SpreadCost { get; set; }

    /// <summary>Commission and fees charged by the broker.</summary>
    public decimal CommissionCost { get; set; }

    /// <summary>Total all-in execution cost (shortfall + spread + commission).</summary>
    public decimal TotalCost { get; set; }

    /// <summary>Total cost expressed in basis points of the notional value.</summary>
    public decimal TotalCostBps { get; set; }

    /// <summary>Lot size of the order.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Elapsed milliseconds from signal creation to fill.</summary>
    public long SignalToFillMs { get; set; }

    /// <summary>Elapsed milliseconds from order submission to fill.</summary>
    public long SubmissionToFillMs { get; set; }

    /// <summary>When the analysis was computed.</summary>
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;

    public virtual Order Order { get; set; } = null!;
    public virtual TradeSignal? TradeSignal { get; set; }

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
