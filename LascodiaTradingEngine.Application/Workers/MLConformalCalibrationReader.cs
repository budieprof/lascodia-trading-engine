using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

public sealed class MLConformalCalibrationReader : IMLConformalCalibrationReader
{
    public async Task<IReadOnlyDictionary<long, MLConformalCalibration>> LoadLatestUsableByModelAsync(
        DbContext db,
        IReadOnlyCollection<MLModel> models,
        ConformalCalibrationSelectionOptions options,
        CancellationToken ct)
    {
        if (models.Count == 0)
            return new Dictionary<long, MLConformalCalibration>();

        var modelById = models.ToDictionary(m => m.Id);
        var modelIds = modelById.Keys.ToArray();
        var oldestAllowed = options.NowUtc.AddDays(-Math.Max(1, options.MaxCalibrationAgeDays));

        var calibrations = await db.Set<MLConformalCalibration>()
            .AsNoTracking()
            .Where(c => modelIds.Contains(c.MLModelId)
                        && !c.IsDeleted
                        && c.CalibrationSamples >= options.MinSamples
                        && c.TargetCoverage > 0.0
                        && c.TargetCoverage < 1.0
                        && c.CoverageThreshold >= 0.0
                        && c.CoverageThreshold <= 1.0
                        && c.CalibratedAt >= oldestAllowed)
            .OrderByDescending(c => c.CalibratedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(ct);

        return calibrations
            .Where(c => modelById.TryGetValue(c.MLModelId, out var model)
                        && IsMatchingModel(c, model)
                        && (!options.RequireCalibrationAfterModelActivation
                            || model.ActivatedAt is null
                            || c.CalibratedAt >= model.ActivatedAt.Value))
            .GroupBy(c => c.MLModelId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    internal static ConformalCalibrationSkipReason? GetSkipReason(
        MLModel model,
        MLConformalCalibration? calibration,
        ConformalCalibrationSelectionOptions options)
    {
        if (calibration is null)
            return ConformalCalibrationSkipReason.Missing;
        if (calibration.CalibrationSamples < options.MinSamples)
            return ConformalCalibrationSkipReason.LowSampleCount;
        if (!IsStrictProbability(calibration.TargetCoverage))
            return ConformalCalibrationSkipReason.InvalidTargetCoverage;
        if (!IsFiniteProbability(calibration.CoverageThreshold))
            return ConformalCalibrationSkipReason.InvalidCoverageThreshold;
        if (!string.Equals(calibration.Symbol?.Trim(), model.Symbol?.Trim(), StringComparison.OrdinalIgnoreCase))
            return ConformalCalibrationSkipReason.SymbolMismatch;
        if (calibration.Timeframe != model.Timeframe)
            return ConformalCalibrationSkipReason.TimeframeMismatch;
        if (calibration.CalibratedAt < options.NowUtc.AddDays(-Math.Max(1, options.MaxCalibrationAgeDays)))
            return ConformalCalibrationSkipReason.StaleCalibration;
        if (options.RequireCalibrationAfterModelActivation
            && model.ActivatedAt.HasValue
            && calibration.CalibratedAt < model.ActivatedAt.Value)
            return ConformalCalibrationSkipReason.BeforeModelActivation;

        return null;
    }

    private static bool IsMatchingModel(MLConformalCalibration calibration, MLModel model)
        => calibration.Timeframe == model.Timeframe
           && string.Equals(
               calibration.Symbol?.Trim(),
               model.Symbol?.Trim(),
               StringComparison.OrdinalIgnoreCase);

    private static bool IsStrictProbability(double value)
        => double.IsFinite(value) && value > 0.0 && value < 1.0;

    private static bool IsFiniteProbability(double value)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0;
}

internal enum ConformalCalibrationSkipReason
{
    Missing,
    LowSampleCount,
    InvalidTargetCoverage,
    InvalidCoverageThreshold,
    SymbolMismatch,
    TimeframeMismatch,
    StaleCalibration,
    BeforeModelActivation
}
