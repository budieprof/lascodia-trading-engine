using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SystemHealth.Queries.GetDefaultsCalibrationReport;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Builds a calibration report for the default floors introduced by the pipeline
/// improvements: walk-forward per-fold minimums, live-vs-backtest Sharpe ratio gate,
/// health-snapshot min-trades guard, Deflated Sharpe threshold, and evaluation-window
/// size. Does not mutate state — the operator reviews the recommendations and applies
/// them via the config upsert endpoint.
/// </summary>
public sealed class GetDefaultsCalibrationReportQuery : IRequest<ResponseData<DefaultsCalibrationReportDto>>
{
    /// <summary>
    /// Number of days of history to include. Defaults to 180 days — long enough to span
    /// multiple market regimes on most instruments but short enough to reject stale pre-
    /// current-lifecycle records. Clamped to [30, 3650].
    /// </summary>
    public int LookbackDays { get; init; } = 180;

    /// <summary>
    /// Minimum sample count before a distribution-based recommendation is emitted.
    /// Defaults to 30 — below this percentiles are unreliable. Clamped to [5, 1000].
    /// </summary>
    public int MinSamplesForRecommendation { get; init; } = 30;
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Reads historical <see cref="WalkForwardRun"/>, <see cref="BacktestRun"/> and
/// <see cref="StrategyPerformanceSnapshot"/> records, computes percentile distributions
/// for each defaulted floor, looks up the currently-configured value from
/// <see cref="EngineConfig"/>, and returns a per-default recommendation.
///
/// <para>
/// Recommendation rules (uniform across every entry):
/// <list type="bullet">
///   <item><b>Insufficient data</b> (sample &lt; <c>MinSamplesForRecommendation</c>): floor unchanged.</item>
///   <item><b>Too tight</b> (&gt; 20% exclusion rate): recommend lowering to the P5 of the distribution.</item>
///   <item><b>Not binding</b> (&lt; 1% exclusion rate, sample &gt; 100): recommend raising to P10.</item>
///   <item><b>In band</b> (1–20% exclusion): floor unchanged.</item>
/// </list>
/// The recommendation is advisory only — it is emitted so the operator can eyeball the
/// distribution against the currently configured floor without having to write SQL.
/// </para>
/// </summary>
public sealed class GetDefaultsCalibrationReportQueryHandler
    : IRequestHandler<GetDefaultsCalibrationReportQuery, ResponseData<DefaultsCalibrationReportDto>>
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly TimeProvider _timeProvider;

    public GetDefaultsCalibrationReportQueryHandler(
        IReadApplicationDbContext readContext,
        TimeProvider timeProvider)
    {
        _readContext = readContext;
        _timeProvider = timeProvider;
    }

    public async Task<ResponseData<DefaultsCalibrationReportDto>> Handle(
        GetDefaultsCalibrationReportQuery request,
        CancellationToken cancellationToken)
    {
        int lookbackDays = Math.Clamp(request.LookbackDays, 30, 3650);
        int minSamples = Math.Clamp(request.MinSamplesForRecommendation, 5, 1000);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var fromUtc = now.AddDays(-lookbackDays);

        var db = _readContext.GetDbContext();

        // ── Pre-load the historical series we need. Each is a single projection so the
        //    aggregation logic below runs on in-memory lists and stays testable.
        var walkForwardRuns = await db.Set<WalkForwardRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && r.Status == RunStatus.Completed
                     && r.StartedAt >= fromUtc)
            .Select(r => new { r.Id, r.InSampleDays, r.OutOfSampleDays, r.WindowResultsJson })
            .ToListAsync(cancellationToken);

        var backtestRuns = await db.Set<BacktestRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && r.Status == RunStatus.Completed
                     && r.CreatedAt >= fromUtc
                     && r.SharpeRatio != null
                     && r.TotalTrades != null && r.TotalTrades > 1)
            .Select(r => new { r.StrategyId, r.SharpeRatio, r.TotalTrades })
            .ToListAsync(cancellationToken);

        var snapshots = await db.Set<StrategyPerformanceSnapshot>()
            .AsNoTracking()
            .Where(s => !s.IsDeleted && s.EvaluatedAt >= fromUtc)
            .Select(s => new { s.StrategyId, s.WindowTrades, s.SharpeRatio, s.HealthStatus })
            .ToListAsync(cancellationToken);

        var configEntries = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => !c.IsDeleted)
            .Select(c => new { c.Key, c.Value })
            .ToListAsync(cancellationToken);
        var configByKey = configEntries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

        var entries = new List<DefaultCalibrationEntryDto>();

        // ── WalkForward:MinInSampleDays ───────────────────────────────────────
        entries.Add(BuildEntry(
            configKey: "WalkForward:MinInSampleDays",
            floorDescription: "Minimum in-sample days per walk-forward run",
            dataSource: $"WalkForwardRun.InSampleDays (completed, last {lookbackDays}d)",
            samples: walkForwardRuns.Select(r => (decimal)r.InSampleDays).ToList(),
            configByKey: configByKey,
            defaultFloor: 14m,
            minSamples: minSamples));

        // ── WalkForward:MinOutOfSampleDays ────────────────────────────────────
        entries.Add(BuildEntry(
            configKey: "WalkForward:MinOutOfSampleDays",
            floorDescription: "Minimum out-of-sample days per walk-forward run",
            dataSource: $"WalkForwardRun.OutOfSampleDays (completed, last {lookbackDays}d)",
            samples: walkForwardRuns.Select(r => (decimal)r.OutOfSampleDays).ToList(),
            configByKey: configByKey,
            defaultFloor: 7m,
            minSamples: minSamples));

        // ── WalkForward:MinTradesPerFold ──────────────────────────────────────
        var perFoldTradeCounts = new List<decimal>();
        foreach (var run in walkForwardRuns)
        {
            if (string.IsNullOrWhiteSpace(run.WindowResultsJson))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(run.WindowResultsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var window in doc.RootElement.EnumerateArray())
                {
                    if (window.TryGetProperty("OosTotalTrades", out var oos)
                        && oos.TryGetInt32(out var count))
                    {
                        perFoldTradeCounts.Add(count);
                    }
                }
            }
            catch (JsonException) { /* skip malformed */ }
        }
        entries.Add(BuildEntry(
            configKey: "WalkForward:MinTradesPerFold",
            floorDescription: "Minimum trades per OOS fold for reliable Sharpe",
            dataSource: $"WalkForwardRun.WindowResultsJson.OosTotalTrades (last {lookbackDays}d)",
            samples: perFoldTradeCounts,
            configByKey: configByKey,
            defaultFloor: 5m,
            minSamples: minSamples));

        // ── StrategyPromotion:MinLiveVsBacktestSharpeRatio ────────────────────
        // One (live Sharpe / backtest Sharpe) ratio per strategy that has both.
        var liveSharpeByStrategy = snapshots
            .Where(s => s.StrategyId > 0)
            .GroupBy(s => s.StrategyId)
            .ToDictionary(g => g.Key, g => g.Average(x => x.SharpeRatio));
        var backtestSharpeByStrategy = backtestRuns
            .Where(r => r.SharpeRatio.HasValue && r.SharpeRatio.Value > 0)
            .GroupBy(r => r.StrategyId)
            .ToDictionary(g => g.Key, g => g.Max(x => x.SharpeRatio!.Value));
        var sharpeRatios = new List<decimal>();
        foreach (var (stratId, live) in liveSharpeByStrategy)
        {
            if (backtestSharpeByStrategy.TryGetValue(stratId, out var backtest) && backtest > 0)
                sharpeRatios.Add(live / backtest);
        }
        entries.Add(BuildEntry(
            configKey: "StrategyPromotion:MinLiveVsBacktestSharpeRatio",
            floorDescription: "Fraction of backtest Sharpe retained in live performance",
            dataSource: $"avg(snapshot Sharpe) / max(BacktestRun Sharpe) per strategy (last {lookbackDays}d)",
            samples: sharpeRatios,
            configByKey: configByKey,
            defaultFloor: 0.5m,
            minSamples: minSamples));

        // ── StrategyHealth:MinTradesForCritical ───────────────────────────────
        // Distribution of WindowTrades across all snapshots whose health score would
        // have tripped Critical — the floor should be well below the P5 of this so
        // genuine Critical signals aren't downgraded.
        var criticalSamples = snapshots
            .Where(s => s.HealthStatus == StrategyHealthStatus.Critical)
            .Select(s => (decimal)s.WindowTrades)
            .ToList();
        entries.Add(BuildEntry(
            configKey: "StrategyHealth:MinTradesForCritical",
            floorDescription: "Min filled-order count before Critical health is honoured",
            dataSource: $"StrategyPerformanceSnapshot.WindowTrades where HealthStatus=Critical (last {lookbackDays}d)",
            samples: criticalSamples,
            configByKey: configByKey,
            defaultFloor: 20m,
            minSamples: minSamples));

        // ── StrategyHealth:EvaluationWindowSize ───────────────────────────────
        var windowTradeSamples = snapshots.Select(s => (decimal)s.WindowTrades).ToList();
        entries.Add(BuildEntry(
            configKey: "StrategyHealth:EvaluationWindowSize",
            floorDescription: "Rolling window size (filled orders) per health evaluation",
            dataSource: $"StrategyPerformanceSnapshot.WindowTrades (all statuses, last {lookbackDays}d)",
            samples: windowTradeSamples,
            configByKey: configByKey,
            defaultFloor: 50m,
            minSamples: minSamples));

        // ── StrategyGeneration:MinDeflatedSharpe ──────────────────────────────
        // Compute DSR for each completed backtest using the canonical helper —
        // candidate count defaults to 10 as a conservative floor (same logic used at
        // screening time). Operators see what a reasonable DSR threshold looks like on
        // their data; the default ships at 0 (disabled) so this report is the only way
        // to pick a non-trivial value.
        var dsrSamples = new List<decimal>();
        foreach (var r in backtestRuns)
        {
            if (!r.SharpeRatio.HasValue || !r.TotalTrades.HasValue) continue;
            double dsr = Strategies.Services.PromotionGateValidator.ComputeDeflatedSharpe(
                rawSharpe: (double)r.SharpeRatio.Value,
                trials: 10,
                trades: r.TotalTrades.Value);
            if (!double.IsNaN(dsr) && !double.IsInfinity(dsr))
                dsrSamples.Add((decimal)dsr);
        }
        entries.Add(BuildEntry(
            configKey: "StrategyGeneration:MinDeflatedSharpe",
            floorDescription: "Minimum Deflated Sharpe Ratio (Bailey/López de Prado) for screening",
            dataSource: $"PromotionGateValidator.ComputeDeflatedSharpe(trials=10) over completed BacktestRun (last {lookbackDays}d)",
            samples: dsrSamples,
            configByKey: configByKey,
            defaultFloor: 0.0m,
            minSamples: minSamples));

        var report = new DefaultsCalibrationReportDto(
            GeneratedAtUtc: now,
            AnalysisFromUtc: fromUtc,
            AnalysisToUtc: now,
            Defaults: entries);

        return ResponseData<DefaultsCalibrationReportDto>.Init(report, true, "Successful", "00");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Internal visibility for unit tests — the recommendation rule is the most
    /// interesting pure function in the handler and is exercised directly.
    /// </summary>
    internal static DefaultCalibrationEntryDto BuildEntry(
        string configKey,
        string floorDescription,
        string dataSource,
        List<decimal> samples,
        IReadOnlyDictionary<string, string> configByKey,
        decimal defaultFloor,
        int minSamples)
    {
        decimal currentFloor = TryReadDecimalFromConfig(configByKey, configKey, defaultFloor);

        if (samples.Count < minSamples)
        {
            return new DefaultCalibrationEntryDto(
                ConfigKey: configKey,
                FloorDescription: floorDescription,
                DataSource: dataSource,
                SampleCount: samples.Count,
                CurrentFloor: currentFloor,
                Distribution: null,
                ExclusionRatePct: 0m,
                RecommendedFloor: currentFloor,
                RecommendationRationale: $"Insufficient data ({samples.Count} samples < minimum {minSamples}) — no change recommended.");
        }

        samples.Sort();
        var distribution = new DistributionDto(
            Min:  samples[0],
            P5:   Percentile(samples, 0.05),
            P10:  Percentile(samples, 0.10),
            P25:  Percentile(samples, 0.25),
            P50:  Percentile(samples, 0.50),
            P75:  Percentile(samples, 0.75),
            P90:  Percentile(samples, 0.90),
            Max:  samples[^1],
            Mean: samples.Sum() / samples.Count);

        int excluded = samples.Count(s => s < currentFloor);
        decimal exclusionRatePct = samples.Count > 0
            ? Math.Round((decimal)excluded * 100m / samples.Count, 2)
            : 0m;

        return DecideRecommendation(
            configKey, floorDescription, dataSource, samples.Count,
            currentFloor, distribution, exclusionRatePct);
    }

    /// <summary>
    /// Pure recommendation decision — kept separate so unit tests can exercise each band
    /// without having to fabricate a full sample list.
    /// </summary>
    internal static DefaultCalibrationEntryDto DecideRecommendation(
        string configKey,
        string floorDescription,
        string dataSource,
        int sampleCount,
        decimal currentFloor,
        DistributionDto distribution,
        decimal exclusionRatePct)
    {
        decimal recommendation;
        string rationale;

        if (exclusionRatePct > 20m)
        {
            recommendation = distribution.P5;
            rationale =
                $"Too tight: current floor {currentFloor:F3} excludes {exclusionRatePct:F1}% of {sampleCount} samples. " +
                $"Recommend lowering to P5 = {distribution.P5:F3} (excludes ~5%).";
        }
        else if (exclusionRatePct < 1m && sampleCount > 100)
        {
            recommendation = distribution.P10;
            rationale =
                $"Not binding: current floor {currentFloor:F3} excludes only {exclusionRatePct:F1}% of {sampleCount} samples. " +
                $"Could tighten to P10 = {distribution.P10:F3} (excludes ~10%) for stricter filtering.";
        }
        else
        {
            recommendation = currentFloor;
            rationale =
                $"In calibration band: current floor {currentFloor:F3} excludes {exclusionRatePct:F1}% of {sampleCount} samples (target 1–20%). " +
                $"No change recommended.";
        }

        return new DefaultCalibrationEntryDto(
            ConfigKey: configKey,
            FloorDescription: floorDescription,
            DataSource: dataSource,
            SampleCount: sampleCount,
            CurrentFloor: currentFloor,
            Distribution: distribution,
            ExclusionRatePct: exclusionRatePct,
            RecommendedFloor: recommendation,
            RecommendationRationale: rationale);
    }

    /// <summary>
    /// Linear-interpolated percentile on a pre-sorted sample. Returns the sole element
    /// for single-element lists and the two-endpoint average for two elements. Standard
    /// NIST "linear interpolation between closest ranks" definition for larger samples.
    /// </summary>
    internal static decimal Percentile(IReadOnlyList<decimal> sorted, double p)
    {
        if (sorted.Count == 0) return 0m;
        if (sorted.Count == 1) return sorted[0];

        double rank = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];

        decimal weight = (decimal)(rank - lo);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * weight;
    }

    private static decimal TryReadDecimalFromConfig(
        IReadOnlyDictionary<string, string> configByKey,
        string key,
        decimal fallback)
    {
        if (!configByKey.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return fallback;

        return decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
