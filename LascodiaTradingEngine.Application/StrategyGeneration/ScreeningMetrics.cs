using System.Text.Json;
using System.Text.Json.Serialization;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Structured screening metrics persisted alongside auto-generated strategies.
/// Replaces fragile description-string parsing for adaptive threshold computation
/// and performance feedback. Serialised to <see cref="Strategy.ScreeningMetricsJson"/>.
/// </summary>
public sealed record ScreeningMetrics
{
    /// <summary>Schema version for forward/backward compatibility. Bump when adding fields.</summary>
    public const int CurrentSchemaVersion = 5;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    // ── In-sample metrics ───────────────────────────────────────────────

    [JsonPropertyName("isWinRate")]
    public double IsWinRate { get; init; }

    [JsonPropertyName("isProfitFactor")]
    public double IsProfitFactor { get; init; }

    [JsonPropertyName("isSharpeRatio")]
    public double IsSharpeRatio { get; init; }

    [JsonPropertyName("isMaxDrawdownPct")]
    public double IsMaxDrawdownPct { get; init; }

    [JsonPropertyName("isTotalTrades")]
    public int IsTotalTrades { get; init; }

    // ── Out-of-sample metrics ───────────────────────────────────────────

    [JsonPropertyName("oosWinRate")]
    public double OosWinRate { get; init; }

    [JsonPropertyName("oosProfitFactor")]
    public double OosProfitFactor { get; init; }

    [JsonPropertyName("oosSharpeRatio")]
    public double OosSharpeRatio { get; init; }

    [JsonPropertyName("oosMaxDrawdownPct")]
    public double OosMaxDrawdownPct { get; init; }

    [JsonPropertyName("oosTotalTrades")]
    public int OosTotalTrades { get; init; }

    // ── Quality gates ───────────────────────────────────────────────────

    [JsonPropertyName("equityCurveR2")]
    public double EquityCurveR2 { get; init; }

    [JsonPropertyName("monteCarloP")]
    public double MonteCarloPValue { get; init; }

    [JsonPropertyName("shufflePValue")]
    public double ShufflePValue { get; init; }

    [JsonPropertyName("walkForwardWindowsPassed")]
    public int WalkForwardWindowsPassed { get; init; }

    /// <summary>
    /// Bitmask of which walk-forward windows passed (bit 0 = window 1, bit 1 = window 2, etc.).
    /// Enables post-hoc analysis of systematic early/late window failures across candidates.
    /// </summary>
    [JsonPropertyName("walkForwardWindowsMask")]
    public int WalkForwardWindowsMask { get; init; }

    [JsonPropertyName("maxTradeTimeConcentration")]
    public double MaxTradeTimeConcentration { get; init; }

    // ── Context ─────────────────────────────────────────────────────────

    [JsonPropertyName("regime")]
    public string Regime { get; init; } = string.Empty;

    [JsonPropertyName("generationSource")]
    public string GenerationSource { get; init; } = "Primary";

    [JsonPropertyName("observedRegime")]
    public string? ObservedRegime { get; init; }

    [JsonPropertyName("reserveTargetRegime")]
    public string? ReserveTargetRegime { get; init; }

    [JsonPropertyName("screenedAtUtc")]
    public DateTime ScreenedAtUtc { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("monteCarloSeed")]
    public int MonteCarloSeed { get; init; }

    // ── New v5 gates ───────────────────────────────────────────────────

    [JsonPropertyName("marginalSharpeContribution")]
    public double MarginalSharpeContribution { get; init; }

    [JsonPropertyName("kellySharpeRatio")]
    public double KellySharpeRatio { get; init; }

    [JsonPropertyName("fixedLotSharpeRatio")]
    public double FixedLotSharpeRatio { get; init; }

    [JsonPropertyName("isAutoPromoted")]
    public bool IsAutoPromoted { get; init; }

    [JsonPropertyName("liveHaircutApplied")]
    public bool LiveHaircutApplied { get; init; }

    [JsonPropertyName("winRateHaircutApplied")]
    public double WinRateHaircutApplied { get; init; } = 1.0;

    [JsonPropertyName("profitFactorHaircutApplied")]
    public double ProfitFactorHaircutApplied { get; init; } = 1.0;

    [JsonPropertyName("sharpeHaircutApplied")]
    public double SharpeHaircutApplied { get; init; } = 1.0;

    [JsonPropertyName("drawdownInflationApplied")]
    public double DrawdownInflationApplied { get; init; } = 1.0;

    [JsonPropertyName("cycleId")]
    public string? CycleId { get; init; }

    [JsonPropertyName("candidateId")]
    public string? CandidateId { get; init; }

    [JsonPropertyName("selectionScore")]
    public double SelectionScore { get; init; }

    [JsonPropertyName("selectionScoreBreakdown")]
    public CandidateSelectionScoreBreakdown? SelectionScoreBreakdown { get; init; }

    [JsonPropertyName("validationPriority")]
    public int ValidationPriority { get; init; }

    [JsonPropertyName("prunedAtUtc")]
    public DateTime? PrunedAtUtc { get; init; }

    // ── Pipeline trace ─────────────────────────────────────────────────

    [JsonPropertyName("gateTrace")]
    public IReadOnlyList<ScreeningGateTrace>? GateTrace { get; init; }

    // ── Serialisation helpers ───────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ScreeningMetrics? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var metrics = JsonSerializer.Deserialize<ScreeningMetrics>(json, JsonOptions);
            if (metrics == null) return null;

            // Migration-on-read: upgrade old schema versions to current.
            return metrics.SchemaVersion switch
            {
                CurrentSchemaVersion => metrics,
                4 => MigrateFromV4(metrics),
                3 => MigrateFromV3(metrics),
                >= 2 => MigrateFromV2(metrics),
                // v0/v1 rows lack SchemaVersion and may have zeroed new fields.
                // If they have valid data, migrate; otherwise return null to avoid
                // skewing adaptive thresholds with zero-valued IS Sharpe/WinRate.
                _ when metrics.IsWinRate > 0 || metrics.IsTotalTrades > 0 => MigrateFromV1(metrics),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>Migrates v1 (pre-schema) metrics: preserves IS/OOS data, marks missing gates as unevaluated.</summary>
    private static ScreeningMetrics MigrateFromV1(ScreeningMetrics old) => old with
    {
        SchemaVersion = CurrentSchemaVersion,
        // v1 lacked walk-forward and Monte Carlo fields — mark as unevaluated
        WalkForwardWindowsPassed = old.WalkForwardWindowsPassed > 0 ? old.WalkForwardWindowsPassed : -1,
        WalkForwardWindowsMask = old.WalkForwardWindowsMask > 0 ? old.WalkForwardWindowsMask : 0,
        EquityCurveR2 = old.EquityCurveR2 != 0 ? old.EquityCurveR2 : -1.0,
    };

    /// <summary>Migrates v4 metrics: adds marginal Sharpe, Kelly/fixed-lot Sharpe, auto-promote, and live haircut fields.</summary>
    private static ScreeningMetrics MigrateFromV4(ScreeningMetrics old) => old with
    {
        SchemaVersion = CurrentSchemaVersion,
        MarginalSharpeContribution = 0,
        KellySharpeRatio = 0,
        FixedLotSharpeRatio = 0,
        IsAutoPromoted = false,
        LiveHaircutApplied = false,
        WinRateHaircutApplied = 1.0,
        ProfitFactorHaircutApplied = 1.0,
        SharpeHaircutApplied = 1.0,
        DrawdownInflationApplied = 1.0,
    };

    /// <summary>Migrates v3 metrics: adds GateTrace field (null for pre-v4 rows).</summary>
    private static ScreeningMetrics MigrateFromV3(ScreeningMetrics old) => old with
    {
        SchemaVersion = CurrentSchemaVersion,
        // GateTrace was not recorded in v3 — leave as null
    };

    /// <summary>Migrates v2 metrics: all IS/OOS and quality gate fields exist, just bump version.</summary>
    private static ScreeningMetrics MigrateFromV2(ScreeningMetrics old) => old with
    {
        SchemaVersion = CurrentSchemaVersion,
    };
}
