using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Consolidated audit record capturing the complete rationale for a specific trade:
/// strategy signal conditions, ML prediction details, all risk check results, and
/// execution quality metrics. Required for MiFID II Article 27 best-execution reporting
/// and institutional audit trails.
/// </summary>
public class TradeRationale : Entity<long>
{
    /// <summary>FK to the order this rationale explains.</summary>
    public long OrderId { get; set; }

    /// <summary>FK to the trade signal that originated this order.</summary>
    public long TradeSignalId { get; set; }

    /// <summary>FK to the strategy that generated the signal.</summary>
    public long StrategyId { get; set; }

    /// <summary>FK to the trading account that placed the order.</summary>
    public long TradingAccountId { get; set; }

    /// <summary>Symbol traded.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Timeframe the strategy evaluated on.</summary>
    public Timeframe Timeframe { get; set; }

    // ── Strategy Signal Context ──────────────────────────────────────────

    /// <summary>Strategy type that generated the signal.</summary>
    public StrategyType StrategyType { get; set; }

    /// <summary>JSON-serialised indicator values at signal time (e.g. RSI, MACD, MA values).</summary>
    public string IndicatorValuesJson { get; set; } = "{}";

    /// <summary>Human-readable description of signal conditions met.</summary>
    public string SignalConditionsMet { get; set; } = string.Empty;

    /// <summary>Rule-based signal direction before ML scoring.</summary>
    public TradeDirection RuleBasedDirection { get; set; }

    /// <summary>Rule-based confidence score (0–1).</summary>
    public decimal RuleBasedConfidence { get; set; }

    // ── ML Prediction Context ──────────────────────────────────────────

    /// <summary>FK to the ML model used for scoring (null if no model active).</summary>
    public long? MLModelId { get; set; }

    /// <summary>ML model version/architecture for audit.</summary>
    public string? MLModelVersion { get; set; }

    /// <summary>ML predicted direction.</summary>
    public TradeDirection? MLPredictedDirection { get; set; }

    /// <summary>Raw probability from the model before calibration.</summary>
    public decimal? MLRawProbability { get; set; }

    /// <summary>Calibrated probability after Platt/isotonic scaling.</summary>
    public decimal? MLCalibratedProbability { get; set; }

    /// <summary>Final served probability after all calibration stages.</summary>
    public decimal? MLServedProbability { get; set; }

    /// <summary>Decision threshold used.</summary>
    public decimal? MLDecisionThreshold { get; set; }

    /// <summary>ML confidence score (0–1).</summary>
    public decimal? MLConfidenceScore { get; set; }

    /// <summary>Top-5 SHAP feature contributions JSON.</summary>
    public string? MLShapContributionsJson { get; set; }

    /// <summary>Ensemble disagreement (std-dev of learner probabilities).</summary>
    public decimal? MLEnsembleDisagreement { get; set; }

    /// <summary>Kelly fraction recommended by the model.</summary>
    public decimal? MLKellyFraction { get; set; }

    // ── Risk Check Results ──────────────────────────────────────────────

    /// <summary>Whether Tier 1 (signal-level) validation passed.</summary>
    public bool Tier1Passed { get; set; }

    /// <summary>Tier 1 block reason (null if passed).</summary>
    public string? Tier1BlockReason { get; set; }

    /// <summary>Whether Tier 2 (account-level) risk check passed.</summary>
    public bool Tier2Passed { get; set; }

    /// <summary>Tier 2 block reason (null if passed).</summary>
    public string? Tier2BlockReason { get; set; }

    /// <summary>
    /// JSON array of all 20 individual risk check results:
    /// [{ "check": "MaxOpenPositions", "passed": true, "value": "3/10" }, ...]
    /// </summary>
    public string RiskCheckDetailsJson { get; set; } = "[]";

    /// <summary>Account equity at time of risk check.</summary>
    public decimal AccountEquityAtCheck { get; set; }

    /// <summary>Portfolio exposure percentage after this trade.</summary>
    public decimal? ProjectedExposurePct { get; set; }

    /// <summary>Risk per trade percentage.</summary>
    public decimal? RiskPerTradePct { get; set; }

    // ── Execution Quality ──────────────────────────────────────────────

    /// <summary>Requested entry price.</summary>
    public decimal RequestedPrice { get; set; }

    /// <summary>Actual fill price (null until filled).</summary>
    public decimal? FillPrice { get; set; }

    /// <summary>Slippage in pips (positive = adverse).</summary>
    public decimal? SlippagePips { get; set; }

    /// <summary>Fill latency in milliseconds.</summary>
    public long? FillLatencyMs { get; set; }

    /// <summary>Spread at time of execution.</summary>
    public decimal? SpreadAtExecution { get; set; }

    // ── Market Context ──────────────────────────────────────────────────

    /// <summary>Market regime at signal time.</summary>
    public MarketRegime? MarketRegimeAtSignal { get; set; }

    /// <summary>Market regime confidence.</summary>
    public decimal? RegimeConfidence { get; set; }

    /// <summary>Active trading session at signal time.</summary>
    public TradingSession? TradingSessionAtSignal { get; set; }

    /// <summary>When this rationale was assembled.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public virtual Order Order { get; set; } = null!;
    public virtual TradeSignal TradeSignal { get; set; } = null!;
    public virtual Strategy Strategy { get; set; } = null!;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
