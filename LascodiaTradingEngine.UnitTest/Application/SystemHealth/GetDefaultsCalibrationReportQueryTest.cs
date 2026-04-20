using LascodiaTradingEngine.Application.SystemHealth.Queries.GetDefaultsCalibrationReport;

namespace LascodiaTradingEngine.UnitTest.Application.SystemHealth;

/// <summary>
/// Unit tests for the pure math in <see cref="GetDefaultsCalibrationReportQueryHandler"/> —
/// percentile interpolation, the three recommendation bands (too tight / in band / not binding),
/// and the insufficient-sample fallback. The DB-bound path is exercised via integration tests
/// elsewhere; this suite isolates the decisions that actually produce the recommendation text.
/// </summary>
public class GetDefaultsCalibrationReportQueryTest
{
    // ── Percentile math ─────────────────────────────────────────────────────────

    [Fact]
    public void Percentile_EmptyList_ReturnsZero()
    {
        Assert.Equal(0m, GetDefaultsCalibrationReportQueryHandler.Percentile(new List<decimal>(), 0.5));
    }

    [Fact]
    public void Percentile_SingleElement_ReturnsElement()
    {
        Assert.Equal(42m, GetDefaultsCalibrationReportQueryHandler.Percentile(new List<decimal> { 42m }, 0.5));
    }

    [Fact]
    public void Percentile_TenEvenIntegers_P50_IsMidpoint()
    {
        // Sorted: 1..10. Linear interpolation at rank = 0.5 * 9 = 4.5 → between 5 and 6 → 5.5.
        var sorted = Enumerable.Range(1, 10).Select(i => (decimal)i).ToList();
        Assert.Equal(5.5m, GetDefaultsCalibrationReportQueryHandler.Percentile(sorted, 0.5));
    }

    [Fact]
    public void Percentile_TenEvenIntegers_P0_And_P100_AreEndpoints()
    {
        var sorted = Enumerable.Range(1, 10).Select(i => (decimal)i).ToList();
        Assert.Equal(1m,  GetDefaultsCalibrationReportQueryHandler.Percentile(sorted, 0.0));
        Assert.Equal(10m, GetDefaultsCalibrationReportQueryHandler.Percentile(sorted, 1.0));
    }

    [Fact]
    public void Percentile_100Samples_P10_Is_ExpectedValue()
    {
        // Sorted 1..100. rank = 0.10 * 99 = 9.9 → interp(sorted[9]=10, sorted[10]=11, 0.9) = 10.9.
        var sorted = Enumerable.Range(1, 100).Select(i => (decimal)i).ToList();
        Assert.Equal(10.9m, GetDefaultsCalibrationReportQueryHandler.Percentile(sorted, 0.10));
    }

    // ── Recommendation bands ────────────────────────────────────────────────────

    [Fact]
    public void DecideRecommendation_TooTight_RecommendsP5()
    {
        // 25% exclusion rate → too tight → recommend P5.
        var distribution = new DistributionDto(Min: 0m, P5: 3m, P10: 5m, P25: 10m, P50: 20m, P75: 35m, P90: 50m, Max: 100m, Mean: 25m);
        var entry = GetDefaultsCalibrationReportQueryHandler.DecideRecommendation(
            configKey: "Test:Floor",
            floorDescription: "Test",
            dataSource: "synthetic",
            sampleCount: 200,
            currentFloor: 15m,
            distribution: distribution,
            exclusionRatePct: 25m);

        Assert.Equal(3m, entry.RecommendedFloor);
        Assert.Contains("Too tight", entry.RecommendationRationale);
        Assert.Contains("P5", entry.RecommendationRationale);
    }

    [Fact]
    public void DecideRecommendation_NotBinding_LargeSample_RecommendsP10()
    {
        // 0.5% exclusion, 200 samples → not binding → tighten to P10.
        var distribution = new DistributionDto(Min: 0m, P5: 3m, P10: 5m, P25: 10m, P50: 20m, P75: 35m, P90: 50m, Max: 100m, Mean: 25m);
        var entry = GetDefaultsCalibrationReportQueryHandler.DecideRecommendation(
            configKey: "Test:Floor",
            floorDescription: "Test",
            dataSource: "synthetic",
            sampleCount: 200,
            currentFloor: 1m,
            distribution: distribution,
            exclusionRatePct: 0.5m);

        Assert.Equal(5m, entry.RecommendedFloor);
        Assert.Contains("Not binding", entry.RecommendationRationale);
        Assert.Contains("P10", entry.RecommendationRationale);
    }

    [Fact]
    public void DecideRecommendation_NotBinding_SmallSample_HoldsCurrentFloor()
    {
        // 0.5% exclusion but only 60 samples (< 100) → still in band; not enough evidence to tighten.
        var distribution = new DistributionDto(Min: 0m, P5: 3m, P10: 5m, P25: 10m, P50: 20m, P75: 35m, P90: 50m, Max: 100m, Mean: 25m);
        var entry = GetDefaultsCalibrationReportQueryHandler.DecideRecommendation(
            configKey: "Test:Floor",
            floorDescription: "Test",
            dataSource: "synthetic",
            sampleCount: 60,
            currentFloor: 1m,
            distribution: distribution,
            exclusionRatePct: 0.5m);

        Assert.Equal(1m, entry.RecommendedFloor);
        Assert.Contains("In calibration band", entry.RecommendationRationale);
    }

    [Fact]
    public void DecideRecommendation_InBand_HoldsCurrentFloor()
    {
        // 10% exclusion → right in the target band; no change.
        var distribution = new DistributionDto(Min: 0m, P5: 3m, P10: 5m, P25: 10m, P50: 20m, P75: 35m, P90: 50m, Max: 100m, Mean: 25m);
        var entry = GetDefaultsCalibrationReportQueryHandler.DecideRecommendation(
            configKey: "Test:Floor",
            floorDescription: "Test",
            dataSource: "synthetic",
            sampleCount: 200,
            currentFloor: 5m,
            distribution: distribution,
            exclusionRatePct: 10m);

        Assert.Equal(5m, entry.RecommendedFloor);
        Assert.Contains("In calibration band", entry.RecommendationRationale);
    }

    // ── End-to-end BuildEntry behaviour ─────────────────────────────────────────

    [Fact]
    public void BuildEntry_InsufficientSamples_ReturnsNullDistribution_NoChange()
    {
        // 5 samples with minimum 30 → insufficient data path — distribution null, current floor held.
        var samples = new List<decimal> { 1m, 2m, 3m, 4m, 5m };
        var configByKey = new Dictionary<string, string>();

        var entry = GetDefaultsCalibrationReportQueryHandler.BuildEntry(
            configKey: "Test:Floor",
            floorDescription: "Test",
            dataSource: "synthetic",
            samples: samples,
            configByKey: configByKey,
            defaultFloor: 10m,
            minSamples: 30);

        Assert.Null(entry.Distribution);
        Assert.Equal(10m, entry.CurrentFloor);
        Assert.Equal(10m, entry.RecommendedFloor);
        Assert.Equal(5, entry.SampleCount);
        Assert.Contains("Insufficient data", entry.RecommendationRationale);
    }

    [Fact]
    public void BuildEntry_EngineConfigOverridesDefault_IsRespected()
    {
        // When EngineConfig has an entry for the key, that value is treated as the current
        // floor — not the hardcoded default. Here: default=10, configured=15, sample median=20,
        // exclusion rate on the uniform 1..100 sample for floor=15 is 14% → in band → hold 15.
        var samples = Enumerable.Range(1, 100).Select(i => (decimal)i).ToList();
        var configByKey = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Test:Floor"] = "15"
        };

        var entry = GetDefaultsCalibrationReportQueryHandler.BuildEntry(
            configKey: "Test:Floor",
            floorDescription: "Test",
            dataSource: "synthetic",
            samples: samples,
            configByKey: configByKey,
            defaultFloor: 10m,
            minSamples: 30);

        Assert.Equal(15m, entry.CurrentFloor);
        Assert.Equal(14m, entry.ExclusionRatePct); // 14 samples (1..14) are below 15
        Assert.Equal(15m, entry.RecommendedFloor);
    }

    [Fact]
    public void BuildEntry_MalformedConfigValue_FallsBackToDefault()
    {
        // Config entry present but unparseable → fall back to the hardcoded default.
        var samples = Enumerable.Range(1, 100).Select(i => (decimal)i).ToList();
        var configByKey = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Test:Floor"] = "not-a-number"
        };

        var entry = GetDefaultsCalibrationReportQueryHandler.BuildEntry(
            configKey: "Test:Floor",
            floorDescription: "Test",
            dataSource: "synthetic",
            samples: samples,
            configByKey: configByKey,
            defaultFloor: 10m,
            minSamples: 30);

        Assert.Equal(10m, entry.CurrentFloor);
    }
}
