using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Behavioural classification of a dominant rejection pattern. Used to bias the
/// <see cref="EvolutionaryStrategyGenerator"/>'s mutation strength so the search
/// explores differently depending on the failure regime.
/// </summary>
public enum RejectionClass
{
    /// <summary>No rejection data available for the given strategy type.</summary>
    Unknown,

    /// <summary>
    /// Rejections dominated by "not enough signal" — ZeroTrades, IsThreshold, MarginalSharpe,
    /// OosThreshold. The search should explore more aggressively (wider mutation) to break
    /// out of dead parameter zones.
    /// </summary>
    Underfit,

    /// <summary>
    /// Rejections dominated by "too good to be true" or "fragile fit" — Degradation,
    /// MonteCarloShuffle, DeflatedSharpe, PositionSizingSensitivity, WalkForward. The
    /// search should refine locally (narrower mutation) around the current parameter region.
    /// </summary>
    Overfit,

    /// <summary>
    /// No single class dominates; fall back to baseline mutation strength.
    /// </summary>
    Mixed,
}

/// <summary>
/// Aggregates recent <see cref="StrategyGenerationFailure"/> rows per strategy type and
/// classifies each type's dominant rejection pattern as <see cref="RejectionClass"/>. The
/// <see cref="EvolutionaryStrategyGenerator"/> consumes the classification to bias mutation
/// strength toward the exploration behaviour most likely to escape the current bottleneck.
/// </summary>
public interface IRejectionReasonAggregator
{
    Task<IReadOnlyDictionary<StrategyType, RejectionClass>> LoadAsync(CancellationToken ct);
}

[RegisterService(ServiceLifetime.Scoped, typeof(IRejectionReasonAggregator))]
public sealed class RejectionReasonAggregator : IRejectionReasonAggregator
{
    private const int DefaultLookbackDays   = 30;
    private const int MinRejectionsForClass = 10;   // below this, return Unknown
    private const double DominanceThreshold = 0.55; // dominant class must be >=55% of rejections

    private readonly IReadApplicationDbContext _readCtx;
    private readonly ILogger<RejectionReasonAggregator> _logger;

    public RejectionReasonAggregator(
        IReadApplicationDbContext readCtx,
        ILogger<RejectionReasonAggregator> logger)
    {
        _readCtx = readCtx;
        _logger  = logger;
    }

    public async Task<IReadOnlyDictionary<StrategyType, RejectionClass>> LoadAsync(CancellationToken ct)
    {
        var db = _readCtx.GetDbContext();
        var cutoff = DateTime.UtcNow.AddDays(-DefaultLookbackDays);

        var rows = await db.Set<StrategyGenerationFailure>()
            .AsNoTracking()
            .Where(f => !f.IsDeleted && f.CreatedAtUtc >= cutoff)
            .Select(f => new { f.StrategyType, f.FailureStage, f.FailureReason })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return new Dictionary<StrategyType, RejectionClass>();

        // Group by strategy type, then count each failure-reason class.
        var byType = rows.GroupBy(r => r.StrategyType);
        var result = new Dictionary<StrategyType, RejectionClass>();

        foreach (var group in byType)
        {
            int total = 0;
            int underfit = 0;
            int overfit  = 0;
            foreach (var row in group)
            {
                total++;
                var cls = Classify(row.FailureStage);
                if (cls == RejectionClass.Unknown)
                    cls = Classify(row.FailureReason);
                if (cls == RejectionClass.Underfit) underfit++;
                else if (cls == RejectionClass.Overfit) overfit++;
            }

            if (total < MinRejectionsForClass)
            {
                result[group.Key] = RejectionClass.Unknown;
                continue;
            }

            double underfitShare = underfit / (double)total;
            double overfitShare  = overfit  / (double)total;

            if (underfitShare >= DominanceThreshold)      result[group.Key] = RejectionClass.Underfit;
            else if (overfitShare >= DominanceThreshold)  result[group.Key] = RejectionClass.Overfit;
            else                                          result[group.Key] = RejectionClass.Mixed;
        }

        _logger.LogInformation(
            "RejectionReasonAggregator: classified {Count} strategy types over {Days}-day window — {Summary}",
            result.Count, DefaultLookbackDays,
            string.Join(", ", result.Select(kv => $"{kv.Key}={kv.Value}")));

        return result;
    }

    /// <summary>
    /// Map a <see cref="ScreeningFailureReason"/> string name to a <see cref="RejectionClass"/>.
    /// Input is the string form (as persisted on <see cref="StrategyGenerationFailure.FailureReason"/>),
    /// not the enum — the table stores the name for forward-compat across schema versions.
    /// </summary>
    internal static RejectionClass Classify(string failureReason)
    {
        if (!Enum.TryParse<ScreeningFailureReason>(failureReason, ignoreCase: true, out var reason))
            return RejectionClass.Unknown;

        return reason switch
        {
            ScreeningFailureReason.ZeroTradesIS
                or ScreeningFailureReason.ZeroTradesOOS
                or ScreeningFailureReason.IsThreshold
                or ScreeningFailureReason.OosThreshold
                or ScreeningFailureReason.MarginalSharpe
                or ScreeningFailureReason.EquityCurveR2
                => RejectionClass.Underfit,

            ScreeningFailureReason.Degradation
                or ScreeningFailureReason.MonteCarloShuffle
                or ScreeningFailureReason.DeflatedSharpe
                or ScreeningFailureReason.PositionSizingSensitivity
                or ScreeningFailureReason.WalkForward
                or ScreeningFailureReason.MonteCarloSignFlip
                => RejectionClass.Overfit,

            _ => RejectionClass.Unknown,
        };
    }
}
