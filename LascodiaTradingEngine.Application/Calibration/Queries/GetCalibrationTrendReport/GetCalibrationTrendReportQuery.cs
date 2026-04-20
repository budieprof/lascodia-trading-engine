using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Calibration.Queries.GetCalibrationTrendReport;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Produces a recalibration-grade report comparing the latest complete month
/// of rejection activity against the N-month baseline that precedes it. The
/// intended consumer is a quarterly operator review: the report flags
/// (stage, reason) pairs whose rate has shifted materially from baseline so
/// operators can investigate whether the threshold is miscalibrated or the
/// market has genuinely changed.
/// </summary>
/// <remarks>
/// <para>
/// A row flips to <c>IsAnomaly = true</c> when its latest-month share of
/// rejections diverges from baseline by more than the configured
/// <see cref="AnomalyThresholdPct"/> AND the baseline volume is substantive
/// (≥ <see cref="MinBaselineCount"/>). Noise-level (stage, reason) pairs
/// that fire a handful of times are ignored — calibration should focus on
/// gates that fire often enough for drift to be real.
/// </para>
/// <para>
/// This query reads only <c>CalibrationSnapshot</c> rows, so it's fast even
/// on long histories. It performs no writes and is safe to serve from a
/// read-only dashboard.
/// </para>
/// </remarks>
public class GetCalibrationTrendReportQuery : IRequest<ResponseData<CalibrationTrendReportDto>>
{
    /// <summary>Number of complete months to use as the baseline. Default 3.</summary>
    public int BaselineMonths       { get; set; } = 3;

    /// <summary>
    /// Absolute difference in rate (0.0-1.0) between latest-month share and
    /// baseline share that flips <see cref="CalibrationTrendReportRowDto.IsAnomaly"/>.
    /// Default 0.15 (15 percentage points).
    /// </summary>
    public decimal AnomalyThresholdPct { get; set; } = 0.15m;

    /// <summary>
    /// Minimum baseline-period rejection count for a (stage, reason) pair to
    /// be considered for anomaly flagging. Pairs below this floor are
    /// reported but never flagged. Default 30.
    /// </summary>
    public long MinBaselineCount     { get; set; } = 30;
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>Top-level report DTO for <see cref="GetCalibrationTrendReportQuery"/>.</summary>
public class CalibrationTrendReportDto
{
    /// <summary>Inclusive start of the latest-complete-month window (UTC).</summary>
    public DateTime LatestMonthStart    { get; set; }

    /// <summary>Exclusive end of the latest-complete-month window (UTC).</summary>
    public DateTime LatestMonthEnd      { get; set; }

    /// <summary>Inclusive start of the baseline window (UTC).</summary>
    public DateTime BaselineStart       { get; set; }

    /// <summary>Exclusive end of the baseline window (UTC). Equals <see cref="LatestMonthStart"/>.</summary>
    public DateTime BaselineEnd         { get; set; }

    /// <summary>Total rejection count across every (stage, reason) in the latest month.</summary>
    public long     LatestMonthTotal    { get; set; }

    /// <summary>Total rejection count across the baseline window.</summary>
    public long     BaselineTotal       { get; set; }

    /// <summary>Configured anomaly threshold (0.0-1.0).</summary>
    public decimal  AnomalyThresholdPct { get; set; }

    /// <summary>Configured baseline-count floor for anomaly flagging.</summary>
    public long     MinBaselineCount    { get; set; }

    /// <summary>Per-(stage, reason) trend rows, sorted so anomalies + high-volume rows bubble up.</summary>
    public List<CalibrationTrendReportRowDto> Rows { get; set; } = new();
}

/// <summary>One (stage, reason) trend row — latest vs baseline share.</summary>
public class CalibrationTrendReportRowDto
{
    public string  Stage                { get; set; } = string.Empty;
    public string  Reason               { get; set; } = string.Empty;

    /// <summary>Count in the latest complete month.</summary>
    public long    LatestMonthCount     { get; set; }

    /// <summary>Count across the baseline window.</summary>
    public long    BaselineCount        { get; set; }

    /// <summary>Latest month's share of that month's total rejections (0.0-1.0).</summary>
    public decimal LatestMonthSharePct  { get; set; }

    /// <summary>Baseline share of baseline-total rejections (0.0-1.0).</summary>
    public decimal BaselineSharePct     { get; set; }

    /// <summary>
    /// <c>LatestMonthSharePct − BaselineSharePct</c>. Positive = rejection
    /// share is growing vs baseline; negative = shrinking.
    /// </summary>
    public decimal DeltaPct             { get; set; }

    /// <summary>
    /// <c>true</c> when <c>|DeltaPct|</c> exceeds the query's threshold AND
    /// <c>BaselineCount</c> is at or above the query's floor. Operators
    /// should investigate flagged rows during quarterly recalibration.
    /// </summary>
    public bool    IsAnomaly            { get; set; }

    /// <summary>Free-form operator-oriented hint derived from the delta direction.</summary>
    public string? Hint                 { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Aggregates <c>CalibrationSnapshot</c> rows into a latest-month-vs-baseline
/// comparison, one row per <c>(Stage, Reason)</c>. Pure read, no side effects.
/// </summary>
public class GetCalibrationTrendReportQueryHandler
    : IRequestHandler<GetCalibrationTrendReportQuery, ResponseData<CalibrationTrendReportDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public GetCalibrationTrendReportQueryHandler(IReadApplicationDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<ResponseData<CalibrationTrendReportDto>> Handle(
        GetCalibrationTrendReportQuery request, CancellationToken cancellationToken)
    {
        int baselineMonths = Math.Max(1, request.BaselineMonths);
        decimal anomalyThreshold = Math.Clamp(request.AnomalyThresholdPct, 0m, 1m);
        long minBaseline = Math.Max(0, request.MinBaselineCount);

        // Latest complete month = the one before the current calendar month.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var currentMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var latestMonthStart  = currentMonthStart.AddMonths(-1);
        var latestMonthEnd    = currentMonthStart;
        var baselineStart     = latestMonthStart.AddMonths(-baselineMonths);
        var baselineEnd       = latestMonthStart;

        // Pull both windows in one query — data volume is low (one row per
        // stage/reason/month combo, typically < a few hundred per month).
        var rows = await _context.GetDbContext()
            .Set<Domain.Entities.CalibrationSnapshot>()
            .AsNoTracking()
            .Where(s => s.PeriodGranularity == "Monthly"
                     && s.PeriodStart >= baselineStart
                     && s.PeriodStart < latestMonthEnd)
            .ToListAsync(cancellationToken);

        var latestRows   = rows.Where(r => r.PeriodStart >= latestMonthStart && r.PeriodStart < latestMonthEnd);
        var baselineRows = rows.Where(r => r.PeriodStart >= baselineStart    && r.PeriodStart < baselineEnd);

        long latestTotal   = latestRows.Sum(r => r.RejectionCount);
        long baselineTotal = baselineRows.Sum(r => r.RejectionCount);

        // Aggregate per (Stage, Reason) for both windows.
        var latestByKey = latestRows
            .GroupBy(r => (r.Stage, r.Reason))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.RejectionCount));

        var baselineByKey = baselineRows
            .GroupBy(r => (r.Stage, r.Reason))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.RejectionCount));

        // Union of keys — some pairs may appear in only one window.
        var keys = new HashSet<(string Stage, string Reason)>(latestByKey.Keys);
        foreach (var k in baselineByKey.Keys) keys.Add(k);

        var reportRows = new List<CalibrationTrendReportRowDto>(keys.Count);
        foreach (var k in keys)
        {
            long latestCount   = latestByKey.TryGetValue(k, out var lc) ? lc : 0;
            long baselineCount = baselineByKey.TryGetValue(k, out var bc) ? bc : 0;

            decimal latestShare   = latestTotal   > 0 ? (decimal)latestCount   / latestTotal   : 0m;
            decimal baselineShare = baselineTotal > 0 ? (decimal)baselineCount / baselineTotal : 0m;
            decimal delta = latestShare - baselineShare;

            bool isAnomaly = Math.Abs(delta) >= anomalyThreshold && baselineCount >= minBaseline;

            string? hint = !isAnomaly ? null
                : delta > 0
                    ? "Rejection share is rising vs baseline — consider whether the gate is overly aggressive or the underlying condition genuinely worsened."
                    : "Rejection share is falling vs baseline — the gate may be loosening silently or upstream behaviour improved; confirm the intent.";

            reportRows.Add(new CalibrationTrendReportRowDto
            {
                Stage               = k.Stage,
                Reason              = k.Reason,
                LatestMonthCount    = latestCount,
                BaselineCount       = baselineCount,
                LatestMonthSharePct = latestShare,
                BaselineSharePct    = baselineShare,
                DeltaPct            = delta,
                IsAnomaly           = isAnomaly,
                Hint                = hint,
            });
        }

        // Sort: anomalies first (they need review), then by baseline volume
        // descending (high-volume gates matter more operationally).
        reportRows.Sort((a, b) =>
        {
            if (a.IsAnomaly != b.IsAnomaly) return a.IsAnomaly ? -1 : 1;
            return b.BaselineCount.CompareTo(a.BaselineCount);
        });

        var dto = new CalibrationTrendReportDto
        {
            LatestMonthStart    = latestMonthStart,
            LatestMonthEnd      = latestMonthEnd,
            BaselineStart       = baselineStart,
            BaselineEnd         = baselineEnd,
            LatestMonthTotal    = latestTotal,
            BaselineTotal       = baselineTotal,
            AnomalyThresholdPct = anomalyThreshold,
            MinBaselineCount    = minBaseline,
            Rows                = reportRows,
        };

        return ResponseData<CalibrationTrendReportDto>.Init(dto, true, "Successful", "00");
    }
}
