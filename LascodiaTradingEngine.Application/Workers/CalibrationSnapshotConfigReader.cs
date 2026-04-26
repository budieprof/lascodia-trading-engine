using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Hot-reloadable runtime configuration for <c>CalibrationSnapshotWorker</c>. Mirrors
/// <see cref="CalibrationSnapshotOptions"/> but values are resolved at runtime from
/// <see cref="EngineConfig"/> (falling back to options defaults), so operators can tune
/// the worker without a redeploy.
/// </summary>
public sealed record CalibrationSnapshotRuntimeConfig(
    int  PollIntervalHours,
    int  BackfillMonths,
    int  PollJitterSeconds,
    int  FailureBackoffCapShift,
    bool UseCycleLock,
    int  CycleLockTimeoutSeconds,
    int  FleetSystemicConsecutiveFailureCycles,
    int  StalenessAlertHours);

public sealed class CalibrationSnapshotConfigReader(CalibrationSnapshotOptions options)
{
    private const string CK_PollHours          = "Calibration:PollIntervalHours";
    private const string CK_BackfillMonths     = "Calibration:BackfillMonths";
    private const string CK_PollJitterSeconds  = "Calibration:PollJitterSeconds";
    private const string CK_FailureBackoffCap  = "Calibration:FailureBackoffCapShift";
    private const string CK_UseCycleLock       = "Calibration:UseCycleLock";
    private const string CK_CycleLockTimeoutSeconds = "Calibration:CycleLockTimeoutSeconds";
    private const string CK_FleetSystemicCycles = "Calibration:FleetSystemicConsecutiveFailureCycles";
    private const string CK_StalenessAlertHours = "Calibration:StalenessAlertHours";

    public async Task<CalibrationSnapshotRuntimeConfig> LoadAsync(DbContext ctx, CancellationToken ct)
    {
        int pollHours = Math.Clamp(
            await GetIntAsync(ctx, CK_PollHours, options.PollIntervalHours, ct),
            1, 168);
        int backfillMonths = Math.Clamp(
            await GetIntAsync(ctx, CK_BackfillMonths, options.BackfillMonths, ct),
            1, 120);
        int pollJitterSeconds = Math.Clamp(
            await GetIntAsync(ctx, CK_PollJitterSeconds, options.PollJitterSeconds, ct),
            0, 86_400);
        int backoffCapShift = Math.Clamp(
            await GetIntAsync(ctx, CK_FailureBackoffCap, options.FailureBackoffCapShift, ct),
            0, 16);
        bool useCycleLock = await GetBoolAsync(ctx, CK_UseCycleLock, options.UseCycleLock, ct);
        int cycleLockTimeoutSeconds = Math.Clamp(
            await GetIntAsync(ctx, CK_CycleLockTimeoutSeconds, options.CycleLockTimeoutSeconds, ct),
            0, 300);
        int fleetSystemicCycles = Math.Max(
            1,
            await GetIntAsync(ctx, CK_FleetSystemicCycles, options.FleetSystemicConsecutiveFailureCycles, ct));
        int stalenessAlertHours = Math.Max(
            1,
            await GetIntAsync(ctx, CK_StalenessAlertHours, options.StalenessAlertHours, ct));

        return new CalibrationSnapshotRuntimeConfig(
            pollHours,
            backfillMonths,
            pollJitterSeconds,
            backoffCapShift,
            useCycleLock,
            cycleLockTimeoutSeconds,
            fleetSystemicCycles,
            stalenessAlertHours);
    }

    private static async Task<int> GetIntAsync(DbContext ctx, string key, int defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key && !c.IsDeleted, ct);
        if (entry?.Value is null) return defaultValue;
        return int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static async Task<bool> GetBoolAsync(DbContext ctx, string key, bool defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key && !c.IsDeleted, ct);
        if (entry?.Value is null) return defaultValue;
        return bool.TryParse(entry.Value, out var parsed) ? parsed : defaultValue;
    }
}
