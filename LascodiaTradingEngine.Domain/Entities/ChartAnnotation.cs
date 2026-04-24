using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Operator-authored note attached to a specific timestamp on a chart. Makes
/// post-mortems navigable — "we flipped the kill switch here", "EA restart at
/// 14:02 UTC", etc. — without forcing operators to cross-reference the audit
/// trail.
/// </summary>
/// <remarks>
/// Annotations are scoped to a <see cref="Target"/> (chart name, e.g.
/// <c>drawdown</c>, <c>performance</c>, <c>pnl</c>) and optionally a
/// <see cref="Symbol"/>. Unscoped annotations (Symbol null) apply to every
/// symbol filter on the target chart.
/// </remarks>
public class ChartAnnotation : Entity<long>
{
    /// <summary>Chart key the note belongs to — e.g. <c>drawdown</c>, <c>performance</c>, <c>pnl</c>, <c>execution-quality</c>.</summary>
    public string   Target       { get; set; } = string.Empty;

    /// <summary>Optional symbol filter. Null annotations apply to every symbol on the target chart.</summary>
    public string?  Symbol       { get; set; }

    /// <summary>UTC timestamp the annotation points at — not when it was created.</summary>
    public DateTime AnnotatedAt  { get; set; }

    /// <summary>Free-form body. Capped at 500 chars so the chart-layer tooltip stays readable.</summary>
    public string   Body         { get; set; } = string.Empty;

    /// <summary>FK to the <see cref="TradingAccount"/> that authored the note.</summary>
    public long     CreatedBy    { get; set; }

    /// <summary>UTC creation timestamp. Distinct from <see cref="AnnotatedAt"/>.</summary>
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp of the most recent body edit. Null until first update.</summary>
    public DateTime? UpdatedAt   { get; set; }

    /// <summary>Soft-delete flag filtered by the global EF query filter.</summary>
    public bool     IsDeleted    { get; set; }
}
