using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// A/B testing variant of an existing strategy. Runs in shadow mode alongside the
/// live strategy, generating shadow signals with modified parameters. Compared
/// against the live version after accumulating sufficient outcomes.
/// </summary>
public class StrategyVariant : Entity<long>
{
    /// <summary>FK to the base strategy this variant modifies.</summary>
    public long BaseStrategyId { get; set; }

    /// <summary>Human-readable name for this variant (e.g. "MA-200 with tighter SL").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>JSON-serialised parameter overrides applied on top of the base strategy.</summary>
    public string ParameterOverridesJson { get; set; } = "{}";

    /// <summary>Whether this variant is currently generating shadow signals.</summary>
    public bool IsActive { get; set; }

    /// <summary>Number of shadow signals generated so far.</summary>
    public int ShadowSignalCount { get; set; }

    /// <summary>Minimum signals required before comparison is valid.</summary>
    public int RequiredSignals { get; set; } = 50;

    /// <summary>Shadow win rate observed during the variant test.</summary>
    public decimal ShadowWinRate { get; set; }

    /// <summary>Shadow expected value per trade.</summary>
    public decimal ShadowExpectedValue { get; set; }

    /// <summary>Shadow Sharpe ratio.</summary>
    public decimal ShadowSharpeRatio { get; set; }

    /// <summary>Base strategy's win rate during the same period for fair comparison.</summary>
    public decimal BaseWinRate { get; set; }

    /// <summary>Base strategy's expected value during the same period.</summary>
    public decimal BaseExpectedValue { get; set; }

    /// <summary>Whether the variant has been promoted to replace the base strategy.</summary>
    public bool IsPromoted { get; set; }

    /// <summary>Human-readable comparison result.</summary>
    public string? ComparisonResultJson { get; set; }

    /// <summary>When the variant test started.</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the variant test completed (null if still running).</summary>
    public DateTime? CompletedAt { get; set; }

    public virtual Strategy BaseStrategy { get; set; } = null!;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
