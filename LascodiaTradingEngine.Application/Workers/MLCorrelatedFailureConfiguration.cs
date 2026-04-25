using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

public enum MLCorrelatedFailureMetric
{
    DirectionAccuracy = 0,
    Profitability = 1,
    Composite = 2
}

public sealed record MLCorrelatedFailureRuntimeConfig(
    int PollSeconds,
    double AlarmRatio,
    double RecoveryRatio,
    int MinModelsForAlarm,
    double FailureThreshold,
    int WindowDays,
    int MinPredictions,
    int StateChangeCooldownMinutes,
    int ModelStatsBatchSize,
    MLCorrelatedFailureMetric FailureMetric);

public sealed class MLCorrelatedFailureConfigReader(MLCorrelatedFailureOptions options)
{
    private const string CK_PollSecs = "MLCorrelated:PollIntervalSeconds";
    private const string CK_AlarmRatio = "MLCorrelated:AlarmRatio";
    private const string CK_RecoveryRatio = "MLCorrelated:RecoveryRatio";
    private const string CK_MinModelsForAlarm = "MLCorrelated:MinModelsForAlarm";
    private const string CK_StateChangeCooldownMinutes = "MLCorrelated:StateChangeCooldownMinutes";
    private const string CK_FailureMetric = "MLCorrelated:FailureMetric";
    private const string CK_AccThreshold = "MLTraining:DriftAccuracyThreshold";
    private const string CK_WindowDays = "MLTraining:DriftWindowDays";
    private const string CK_MinPredictions = "MLTraining:DriftMinPredictions";

    private const int DefaultWindowDays = 14;
    private const int DefaultMinPredictions = 30;
    private const double DefaultFailureThreshold = 0.50;

    public async Task<MLCorrelatedFailureRuntimeConfig> LoadAsync(
        DbContext readCtx,
        CancellationToken ct)
    {
        int pollSecs = ClampInt(
            await GetConfigAsync(readCtx, CK_PollSecs, options.PollIntervalSeconds, ct),
            options.PollIntervalSeconds,
            30,
            24 * 60 * 60);
        double alarmRatio = ClampFinite(
            await GetConfigAsync(readCtx, CK_AlarmRatio, options.AlarmRatio, ct),
            options.AlarmRatio,
            0.01,
            1.0);
        double recoveryRatio = ClampFinite(
            await GetConfigAsync(readCtx, CK_RecoveryRatio, options.RecoveryRatio, ct),
            options.RecoveryRatio,
            0.0,
            1.0);
        if (recoveryRatio >= alarmRatio)
        {
            recoveryRatio = Math.Max(0.0, alarmRatio * 0.5);
        }

        var metricName = await GetConfigAsync(readCtx, CK_FailureMetric, options.FailureMetric, ct);
        var metric = Enum.TryParse<MLCorrelatedFailureMetric>(metricName, ignoreCase: true, out var parsedMetric)
            ? parsedMetric
            : MLCorrelatedFailureMetric.DirectionAccuracy;

        return new MLCorrelatedFailureRuntimeConfig(
            PollSeconds: pollSecs,
            AlarmRatio: alarmRatio,
            RecoveryRatio: recoveryRatio,
            MinModelsForAlarm: ClampInt(
                await GetConfigAsync(readCtx, CK_MinModelsForAlarm, options.MinModelsForAlarm, ct),
                options.MinModelsForAlarm,
                2,
                10_000),
            FailureThreshold: ClampFinite(
                await GetConfigAsync(readCtx, CK_AccThreshold, DefaultFailureThreshold, ct),
                DefaultFailureThreshold,
                0.0,
                1.0),
            WindowDays: ClampInt(
                await GetConfigAsync(readCtx, CK_WindowDays, DefaultWindowDays, ct),
                DefaultWindowDays,
                1,
                365),
            MinPredictions: ClampInt(
                await GetConfigAsync(readCtx, CK_MinPredictions, DefaultMinPredictions, ct),
                DefaultMinPredictions,
                1,
                100_000),
            StateChangeCooldownMinutes: ClampInt(
                await GetConfigAsync(readCtx, CK_StateChangeCooldownMinutes, options.StateChangeCooldownMinutes, ct),
                options.StateChangeCooldownMinutes,
                0,
                24 * 60),
            ModelStatsBatchSize: ClampInt(options.ModelStatsBatchSize, 1_000, 1, 10_000),
            FailureMetric: metric);
    }

    private static async Task<T> GetConfigAsync<T>(
        DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key && !c.IsDeleted, ct);

        if (entry?.Value is null) return defaultValue;

        return TryConvertConfig(entry.Value, out T parsed)
            ? parsed
            : defaultValue;
    }

    private static bool TryConvertConfig<T>(string value, out T result)
    {
        object? parsed = null;
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(string))
        {
            parsed = value;
        }
        else if (targetType == typeof(bool) && bool.TryParse(value, out var boolValue))
        {
            parsed = boolValue;
        }
        else if (targetType == typeof(int) &&
                 int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            parsed = intValue;
        }
        else if (targetType == typeof(long) &&
                 long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            parsed = longValue;
        }
        else if (targetType == typeof(double) &&
                 double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            parsed = doubleValue;
        }
        else if (targetType == typeof(decimal) &&
                 decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue))
        {
            parsed = decimalValue;
        }

        if (parsed is T typed)
        {
            result = typed;
            return true;
        }

        result = default!;
        return false;
    }

    private static int ClampInt(int value, int defaultValue, int min, int max)
        => Math.Clamp(value, min, max);

    private static double ClampFinite(double value, double defaultValue, double min, double max)
        => double.IsFinite(value)
            ? Math.Clamp(value, min, max)
            : Math.Clamp(defaultValue, min, max);
}
