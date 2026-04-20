using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyGeneration.Queries.GetScreeningGateBindingReport;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Diagnostic report that identifies which screening gate is the binding
/// constraint on strategy approval over a recent window. The intended use is
/// a targeted tuning decision: instead of guessing which threshold to loosen,
/// the operator sees which gate actually rejected the most candidates and
/// whether the engine is failing underfit (too-tight floors) or overfit
/// (fragile fits).
/// </summary>
/// <remarks>
/// <para>
/// Reads <see cref="Domain.Entities.StrategyGenerationFailure"/> rows within
/// the lookback window and classifies each <see cref="ScreeningFailureReason"/>
/// using the same Underfit/Overfit taxonomy as
/// <see cref="RejectionReasonAggregator"/>. Groups the rows three ways: by
/// reason (gate-level — the binding constraint), by strategy type (which
/// archetypes are worst hit), and by class (overall bias). Only the
/// <see cref="ScreeningFailureReason"/> rows are reported; infrastructure
/// failures (<c>Timeout</c>, <c>TaskFault</c>) are excluded from the binding
/// decision because they don't represent a tunable gate.
/// </para>
/// <para>
/// The recommendation string is a short operator hint keyed to the dominant
/// reason. The recommendation is informational, not prescriptive — the
/// engine does not auto-apply the suggestion, because changing screening
/// thresholds has blast radius beyond what calibration data alone should
/// decide.
/// </para>
/// </remarks>
public class GetScreeningGateBindingReportQuery : IRequest<ResponseData<ScreeningGateBindingReportDto>>
{
    /// <summary>Lookback window in days. Clamped to at least 1. Default 30.</summary>
    public int  LookbackDays      { get; set; } = 30;

    /// <summary>
    /// Minimum total rejections required before the report flags a binding
    /// gate. Below this floor the report returns <see cref="ScreeningGateBindingReportDto.IsReliable"/>
    /// = false so operators don't over-index on thin samples. Default 50.
    /// </summary>
    public int  MinBindingCount   { get; set; } = 50;

    /// <summary>
    /// Share of total rejections at which a single class (Underfit / Overfit)
    /// is declared dominant in <see cref="ScreeningGateBindingReportDto.OverallClass"/>.
    /// Matches the 0.55 default used by <see cref="RejectionReasonAggregator"/>.
    /// </summary>
    public decimal DominanceThreshold { get; set; } = 0.55m;
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>Top-level report DTO for <see cref="GetScreeningGateBindingReportQuery"/>.</summary>
public class ScreeningGateBindingReportDto
{
    public DateTime WindowStart        { get; set; }
    public DateTime WindowEnd          { get; set; }
    public int      LookbackDays       { get; set; }
    public long     TotalFailures      { get; set; }

    /// <summary><c>false</c> when sample size is below <c>MinBindingCount</c>.</summary>
    public bool     IsReliable         { get; set; }

    /// <summary>Overall class across all reasons — Underfit / Overfit / Mixed / Unknown.</summary>
    public string   OverallClass       { get; set; } = nameof(RejectionClass.Unknown);

    /// <summary>Top reason by count (excluding infrastructure failures). Null when sample is empty.</summary>
    public string?  BindingReason      { get; set; }

    /// <summary>Share (0.0-1.0) of total rejections that the binding reason accounts for.</summary>
    public decimal  BindingReasonShare { get; set; }

    /// <summary>Class (Underfit / Overfit / Unknown) of the binding reason.</summary>
    public string   BindingClass       { get; set; } = nameof(RejectionClass.Unknown);

    /// <summary>Short operator-facing hint about what to tune. Null when report is not reliable.</summary>
    public string?  Recommendation     { get; set; }

    /// <summary>Per-reason breakdown, sorted by count descending.</summary>
    public List<ScreeningGateBindingRowDto> Rows { get; set; } = new();
}

/// <summary>One screening-failure-reason row in the binding report.</summary>
public class ScreeningGateBindingRowDto
{
    public string  Reason          { get; set; } = string.Empty;
    public long    Count           { get; set; }
    public decimal SharePct        { get; set; }
    public string  Class           { get; set; } = nameof(RejectionClass.Unknown);

    /// <summary>Strategy type with the most rejections under this reason — a hint for targeted tuning.</summary>
    public string? TopStrategyType { get; set; }
    public long    TopStrategyTypeCount { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetScreeningGateBindingReportQueryHandler
    : IRequestHandler<GetScreeningGateBindingReportQuery, ResponseData<ScreeningGateBindingReportDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public GetScreeningGateBindingReportQueryHandler(
        IReadApplicationDbContext context,
        TimeProvider timeProvider)
    {
        _context      = context;
        _timeProvider = timeProvider;
    }

    public async Task<ResponseData<ScreeningGateBindingReportDto>> Handle(
        GetScreeningGateBindingReportQuery request, CancellationToken cancellationToken)
    {
        int  lookbackDays      = Math.Max(1, request.LookbackDays);
        int  minBinding        = Math.Max(0, request.MinBindingCount);
        decimal dominance      = Math.Clamp(request.DominanceThreshold, 0m, 1m);

        var now    = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-lookbackDays);

        var rows = await _context.GetDbContext()
            .Set<Domain.Entities.StrategyGenerationFailure>()
            .AsNoTracking()
            .Where(f => !f.IsDeleted && f.CreatedAtUtc >= cutoff)
            .Select(f => new { f.FailureReason, f.StrategyType })
            .ToListAsync(cancellationToken);

        long total = rows.Count;

        // Group by reason (gate-level). Also track the top strategy type per
        // reason so operators can see which archetype is hit hardest.
        var byReason = rows
            .GroupBy(r => r.FailureReason)
            .Select(g =>
            {
                var topType = g.GroupBy(x => x.StrategyType)
                               .OrderByDescending(tg => tg.Count())
                               .First();
                return new
                {
                    Reason          = g.Key,
                    Count           = (long)g.Count(),
                    TopStrategyType = topType.Key,
                    TopTypeCount    = (long)topType.Count(),
                };
            })
            .OrderByDescending(r => r.Count)
            .ToList();

        var rowDtos = byReason.Select(r => new ScreeningGateBindingRowDto
        {
            Reason               = r.Reason,
            Count                = r.Count,
            SharePct             = total > 0 ? (decimal)r.Count / total : 0m,
            Class                = RejectionReasonAggregator.Classify(r.Reason).ToString(),
            TopStrategyType      = r.TopStrategyType.ToString(),
            TopStrategyTypeCount = r.TopTypeCount,
        }).ToList();

        long underfit = rowDtos.Where(r => r.Class == nameof(RejectionClass.Underfit)).Sum(r => r.Count);
        long overfit  = rowDtos.Where(r => r.Class == nameof(RejectionClass.Overfit)).Sum(r => r.Count);

        string overallClass = nameof(RejectionClass.Unknown);
        if (total > 0)
        {
            decimal underfitShare = (decimal)underfit / total;
            decimal overfitShare  = (decimal)overfit  / total;
            if (underfitShare >= dominance)      overallClass = nameof(RejectionClass.Underfit);
            else if (overfitShare >= dominance)  overallClass = nameof(RejectionClass.Overfit);
            else if (total >= minBinding)        overallClass = nameof(RejectionClass.Mixed);
        }

        // Binding reason = top count among tunable gates. Exclude pure
        // infrastructure failures from binding-gate candidacy — they don't
        // represent a tunable knob.
        var tunableRows = rowDtos
            .Where(r => !IsInfrastructureReason(r.Reason))
            .ToList();

        string?  bindingReason = tunableRows.FirstOrDefault()?.Reason;
        decimal  bindingShare  = tunableRows.FirstOrDefault() is { } b0 && total > 0
            ? (decimal)b0.Count / total
            : 0m;
        string   bindingClass  = tunableRows.FirstOrDefault()?.Class ?? nameof(RejectionClass.Unknown);

        bool isReliable = total >= minBinding && bindingReason is not null;
        string? recommendation = isReliable
            ? RecommendForReason(bindingReason!)
            : null;

        var dto = new ScreeningGateBindingReportDto
        {
            WindowStart        = cutoff,
            WindowEnd          = now,
            LookbackDays       = lookbackDays,
            TotalFailures      = total,
            IsReliable         = isReliable,
            OverallClass       = overallClass,
            BindingReason      = bindingReason,
            BindingReasonShare = bindingShare,
            BindingClass       = bindingClass,
            Recommendation     = recommendation,
            Rows               = rowDtos,
        };

        return ResponseData<ScreeningGateBindingReportDto>.Init(dto, true, "Successful", "00");
    }

    private static bool IsInfrastructureReason(string reason) =>
        string.Equals(reason, nameof(ScreeningFailureReason.Timeout),   StringComparison.OrdinalIgnoreCase)
     || string.Equals(reason, nameof(ScreeningFailureReason.TaskFault), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps the binding reason to a short action hint. Keep these terse —
    /// they appear in operator dashboards, not documentation.
    /// </summary>
    internal static string RecommendForReason(string reason) =>
        Enum.TryParse<ScreeningFailureReason>(reason, ignoreCase: true, out var r)
            ? r switch
            {
                ScreeningFailureReason.IsThreshold =>
                    "Loosen IS floors (MinWinRate, MinProfitFactor, MinSharpe, MaxDrawdownPct) — candidates are being rejected for not clearing absolute quality bars.",
                ScreeningFailureReason.OosThreshold =>
                    "Loosen OOS relaxation factors (OosPfRelaxation, OosDdRelaxation) — IS-fit candidates are failing on the held-out window.",
                ScreeningFailureReason.ZeroTradesIS or ScreeningFailureReason.ZeroTradesOOS =>
                    "Candidates are producing no trades — the generator is emitting parameter combos whose entry conditions never fire; widen parameter search or revisit evaluator entry logic.",
                ScreeningFailureReason.EquityCurveR2 =>
                    "Loosen MinEquityCurveR² (default 0.70) — equity-curve linearity floor is rejecting choppy but positive-expectancy strategies.",
                ScreeningFailureReason.TimeConcentration =>
                    "Loosen MaxTradeTimeConcentration (default 0.60) — entries are clustering in fewer hours than the gate allows.",
                ScreeningFailureReason.Degradation =>
                    "IS→OOS degradation is the binding reason — overfit signal. Tighten IS thresholds or extend OOS length rather than loosening degradation tolerance.",
                ScreeningFailureReason.WalkForward =>
                    "Walk-forward pass rate is the binding reason — overfit signal. Consider longer windows or different IS/OOS split rather than loosening the 2-of-3 rule.",
                ScreeningFailureReason.MonteCarloSignFlip or ScreeningFailureReason.MonteCarloShuffle =>
                    "Monte Carlo tests are the binding reason — candidates are statistically indistinguishable from random or path-dependent. Tighten upstream IS floors; don't relax MC.",
                ScreeningFailureReason.DeflatedSharpe =>
                    "Deflated Sharpe is the binding reason — search breadth is too wide relative to signal. Reduce candidate volume per cycle or narrow search space.",
                ScreeningFailureReason.PositionSizingSensitivity =>
                    "Position sizing sensitivity is the binding reason — fragile sizing. Review sizing config; do not loosen the sensitivity gate.",
                ScreeningFailureReason.MarginalSharpe =>
                    "Marginal-Sharpe floor is the binding reason — Sharpe is barely positive after costs. Loosen MinSharpe only if costs are already modelled accurately.",
                ScreeningFailureReason.LookaheadAudit =>
                    "Lookahead audit is the binding reason — this is a correctness failure, not a threshold. Investigate evaluator or backtest for future-candle leakage.",
                _ => "No specific recommendation for this reason; investigate directly.",
            }
            : "No specific recommendation for this reason; investigate directly.";
}
