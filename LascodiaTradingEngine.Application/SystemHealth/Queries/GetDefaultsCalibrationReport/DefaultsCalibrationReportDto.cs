namespace LascodiaTradingEngine.Application.SystemHealth.Queries.GetDefaultsCalibrationReport;

/// <summary>
/// Structured recommendation report for tuning the default floors introduced by the
/// pipeline improvements. Each entry carries the observed historical distribution for
/// one floor alongside the currently-configured value, the fraction of historical records
/// that value would exclude, and a recommended adjustment.
///
/// <para>
/// The report is intended to be read once per quarter (or after any significant shift in
/// traded instruments / strategy mix). It does NOT mutate engine state — the operator is
/// expected to review the recommendations and apply them via the
/// <c>/config</c> upsert endpoint.
/// </para>
/// </summary>
public sealed record DefaultsCalibrationReportDto(
    DateTime GeneratedAtUtc,
    DateTime AnalysisFromUtc,
    DateTime AnalysisToUtc,
    IReadOnlyList<DefaultCalibrationEntryDto> Defaults);

/// <summary>
/// One calibration entry per default floor. When <see cref="SampleCount"/> is below the
/// report's minimum-sample threshold the <see cref="Distribution"/> is null and
/// <see cref="RecommendedFloor"/> equals <see cref="CurrentFloor"/> — there isn't enough
/// history to recommend a change.
/// </summary>
public sealed record DefaultCalibrationEntryDto(
    string ConfigKey,
    string FloorDescription,
    string DataSource,
    int SampleCount,
    decimal? CurrentFloor,
    DistributionDto? Distribution,
    decimal ExclusionRatePct,
    decimal? RecommendedFloor,
    string RecommendationRationale);

/// <summary>
/// Percentile summary of an empirical distribution. All values are in the floor's native
/// units (days, trade count, Sharpe ratio, etc.). Percentiles are computed by linear
/// interpolation on the sorted sample and are robust to small samples but become noisy
/// below ~30 observations — the handler surfaces <see cref="DefaultCalibrationEntryDto.SampleCount"/>
/// so the reader can weight confidence accordingly.
/// </summary>
public sealed record DistributionDto(
    decimal Min,
    decimal P5,
    decimal P10,
    decimal P25,
    decimal P50,
    decimal P75,
    decimal P90,
    decimal Max,
    decimal Mean);
