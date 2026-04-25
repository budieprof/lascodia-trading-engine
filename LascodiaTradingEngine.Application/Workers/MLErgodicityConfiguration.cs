using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

public sealed record MLErgodicityRuntimeConfig(
    bool Enabled,
    int InitialDelaySeconds,
    int PollIntervalHours,
    int WindowDays,
    int MinSamples,
    int MaxLogsPerModel,
    int ModelBatchSize,
    int MaxCycleModels,
    int LockTimeoutSeconds,
    int DbCommandTimeoutSeconds,
    double MaxKellyAbs,
    double ReturnPipScale,
    double MaxReturnAbs);

public sealed class MLErgodicityConfigReader(MLErgodicityOptions options)
{
    private const string CK_Enabled = "MLErgodicity:Enabled";
    private const string CK_InitialDelaySeconds = "MLErgodicity:InitialDelaySeconds";
    private const string CK_PollHours = "MLErgodicity:PollIntervalHours";
    private const string CK_WindowDays = "MLErgodicity:WindowDays";
    private const string CK_MinSamples = "MLErgodicity:MinSamples";
    private const string CK_MaxLogsPerModel = "MLErgodicity:MaxLogsPerModel";
    private const string CK_ModelBatchSize = "MLErgodicity:ModelBatchSize";
    private const string CK_MaxCycleModels = "MLErgodicity:MaxCycleModels";
    private const string CK_LockTimeoutSeconds = "MLErgodicity:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLErgodicity:DbCommandTimeoutSeconds";
    private const string CK_MaxKellyAbs = "MLErgodicity:MaxKellyAbs";
    private const string CK_ReturnPipScale = "MLErgodicity:ReturnPipScale";
    private const string CK_MaxReturnAbs = "MLErgodicity:MaxReturnAbs";

    private static readonly string[] ConfigKeys =
    [
        CK_Enabled,
        CK_InitialDelaySeconds,
        CK_PollHours,
        CK_WindowDays,
        CK_MinSamples,
        CK_MaxLogsPerModel,
        CK_ModelBatchSize,
        CK_MaxCycleModels,
        CK_LockTimeoutSeconds,
        CK_DbCommandTimeoutSeconds,
        CK_MaxKellyAbs,
        CK_ReturnPipScale,
        CK_MaxReturnAbs
    ];

    public async Task<MLErgodicityRuntimeConfig> LoadAsync(
        DbContext readCtx,
        CancellationToken ct)
    {
        var configuredValues = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => ConfigKeys.Contains(c.Key) && !c.IsDeleted)
            .Select(c => new { c.Key, c.Value })
            .ToListAsync(ct);

        var values = configuredValues
            .Where(c => c.Value is not null)
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Last().Value!,
                StringComparer.Ordinal);

        int minSamples = ClampInt(GetConfig(values, CK_MinSamples, options.MinSamples), 2, 10_000);
        int maxLogsPerModel = ClampInt(
            GetConfig(values, CK_MaxLogsPerModel, options.MaxLogsPerModel),
            minSamples,
            10_000);

        return new MLErgodicityRuntimeConfig(
            Enabled: GetConfig(values, CK_Enabled, options.Enabled),
            InitialDelaySeconds: ClampInt(
                GetConfig(values, CK_InitialDelaySeconds, options.InitialDelaySeconds),
                0,
                86_400),
            PollIntervalHours: ClampInt(
                GetConfig(values, CK_PollHours, options.PollIntervalHours),
                1,
                168),
            WindowDays: ClampInt(
                GetConfig(values, CK_WindowDays, options.WindowDays),
                1,
                365),
            MinSamples: minSamples,
            MaxLogsPerModel: maxLogsPerModel,
            ModelBatchSize: ClampInt(
                GetConfig(values, CK_ModelBatchSize, options.ModelBatchSize),
                1,
                10_000),
            MaxCycleModels: ClampInt(
                GetConfig(values, CK_MaxCycleModels, options.MaxCycleModels),
                1,
                250_000),
            LockTimeoutSeconds: ClampInt(
                GetConfig(values, CK_LockTimeoutSeconds, options.LockTimeoutSeconds),
                0,
                300),
            DbCommandTimeoutSeconds: ClampInt(
                GetConfig(values, CK_DbCommandTimeoutSeconds, options.DbCommandTimeoutSeconds),
                1,
                600),
            MaxKellyAbs: ClampDouble(
                GetConfig(values, CK_MaxKellyAbs, options.MaxKellyAbs),
                0.01,
                100.0,
                options.MaxKellyAbs),
            ReturnPipScale: ClampDouble(
                GetConfig(values, CK_ReturnPipScale, options.ReturnPipScale),
                0.01,
                100_000.0,
                options.ReturnPipScale),
            MaxReturnAbs: ClampDouble(
                GetConfig(values, CK_MaxReturnAbs, options.MaxReturnAbs),
                0.000001,
                0.999999,
                options.MaxReturnAbs));
    }

    private static T GetConfig<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        T defaultValue)
    {
        if (!values.TryGetValue(key, out var value))
            return defaultValue;

        return TryConvertConfig(value, out T parsed)
            ? parsed
            : defaultValue;
    }

    private static int ClampInt(int value, int min, int max) => Math.Clamp(value, min, max);

    private static double ClampDouble(double value, double min, double max, double fallback)
    {
        var safeValue = double.IsFinite(value) ? value : fallback;
        if (!double.IsFinite(safeValue))
            safeValue = min;

        return Math.Clamp(safeValue, min, max);
    }

    private static bool TryConvertConfig<T>(string value, out T result)
    {
        object? parsed = null;
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(string))
        {
            parsed = value;
        }
        else if (targetType == typeof(int) &&
                 int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            parsed = intValue;
        }
        else if (targetType == typeof(bool) &&
                 TryParseBool(value, out var boolValue))
        {
            parsed = boolValue;
        }
        else if (targetType == typeof(double) &&
                 double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            parsed = doubleValue;
        }

        if (parsed is T typed)
        {
            result = typed;
            return true;
        }

        result = default!;
        return false;
    }

    private static bool TryParseBool(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        return false;
    }
}
