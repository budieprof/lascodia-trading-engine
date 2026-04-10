using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>All hot-reloadable configuration for a single generation cycle.</summary>
public sealed record GenerationConfig
{
    public int ScreeningMonths { get; init; }
    public double MinWinRate { get; init; }
    public double MinProfitFactor { get; init; }
    public int MinTotalTrades { get; init; }
    public double MaxDrawdownPct { get; init; }
    public double MinSharpe { get; init; }

    public int MaxCandidates { get; init; }
    public int MaxActivePerSymbol { get; init; }
    public int MaxActivePerTypePerSymbol { get; init; }
    public int MaxPerCurrencyGroup { get; init; }
    public int MaxCorrelatedCandidates { get; init; }

    public int PruneAfterFailed { get; init; }
    public int RegimeFreshnessHours { get; init; }
    public int RetryCooldownDays { get; init; }
    public int RegimeTransitionCooldownHours { get; init; }

    public double ScreeningSpreadPoints { get; init; }
    public double ScreeningCommissionPerLot { get; init; }
    public double ScreeningSlippagePips { get; init; }
    public double MaxSpreadToRangeRatio { get; init; }
    public decimal ScreeningInitialBalance { get; init; }

    public double MinRegimeConfidence { get; init; }
    public double MaxOosDegradationPct { get; init; }
    public double RegimeBudgetDiversityPct { get; init; }

    public bool SuppressDuringDrawdownRecovery { get; init; }
    public bool SeasonalBlackoutEnabled { get; init; }
    public string BlackoutPeriods { get; init; } = string.Empty;
    public string BlackoutTimezone { get; init; } = "UTC";
    public bool SkipWeekends { get; init; }

    public int ScreeningTimeoutSeconds { get; init; }
    public IReadOnlyList<Timeframe> CandidateTimeframes { get; init; } = [];
    public int MaxTemplatesPerCombo { get; init; }
    public int MaxParallelBacktests { get; init; }
    public int MaxCandleCacheSize { get; init; }
    public int CandleChunkSize { get; init; }
    public int MaxCandleAgeHours { get; init; }

    public double MinEquityCurveR2 { get; init; }
    public double MaxTradeTimeConcentration { get; init; }

    public int WalkForwardWindowCount { get; init; }
    public int WalkForwardMinWindowsPass { get; init; }
    public string WalkForwardSplitPcts { get; init; } = "0.40,0.55,0.70";
    public IReadOnlyList<double> WalkForwardSplitPercentages { get; init; } = [];

    public bool MonteCarloEnabled { get; init; }
    public int MonteCarloPermutations { get; init; }
    public double MonteCarloMinPValue { get; init; }
    public bool MonteCarloShuffleEnabled { get; init; }
    public int MonteCarloShufflePermutations { get; init; }
    public double MonteCarloShuffleMinPValue { get; init; }

    public bool PortfolioBacktestEnabled { get; init; }
    public double MaxPortfolioDrawdownPct { get; init; }
    public double PortfolioCorrelationWeight { get; init; }

    public int StrategicReserveQuota { get; init; }
    public int MaxCandidatesPerWeek { get; init; }

    public bool AdaptiveThresholdsEnabled { get; init; }
    public int AdaptiveThresholdsMinSamples { get; init; }

    public int CircuitBreakerMaxFailures { get; init; }
    public int CircuitBreakerBackoffDays { get; init; }
    public int MaxFaultsPerStrategyType { get; init; }

    public int ActiveStrategyCount { get; set; }

    // ── OOS / position sizing relaxation multipliers (configurable) ──
    public double OosPfRelaxation { get; init; } = 0.9;
    public double OosDdRelaxation { get; init; } = 1.1;
    public double OosSharpeRelaxation { get; init; } = 0.8;
    public double RegimeDegradationRelaxation { get; init; } = 1.5;
    public decimal KellyFactor { get; init; } = 0.5m;
    public decimal KellyMinLot { get; init; } = 0.01m;
    public decimal KellyMaxLot { get; init; } = 0.10m;
}

/// <summary>Adaptive threshold multipliers computed from recent screening distributions.</summary>
public sealed record AdaptiveThresholdAdjustments(
    double WinRateMultiplier,
    double ProfitFactorMultiplier,
    double SharpeMultiplier,
    double DrawdownMultiplier)
{
    public static readonly AdaptiveThresholdAdjustments Neutral = new(1.0, 1.0, 1.0, 1.0);
}

/// <summary>Typed configuration payload returned by the strategy-generation config provider.</summary>
public sealed record StrategyGenerationConfigurationSnapshot(
    GenerationConfig Config,
    Dictionary<string, string> RawConfigs,
    IReadOnlyDictionary<string, StrategyGenerationSymbolOverrides> SymbolOverridesBySymbol);

public sealed record StrategyGenerationSymbolOverrides(
    double? MinWinRate,
    double? MinProfitFactor,
    double? MinSharpe,
    double? MaxDrawdownPct);
