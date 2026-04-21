using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

public sealed record MLErgodicityRuntimeConfig(
    int PollIntervalHours,
    int WindowDays,
    int MinSamples,
    int MaxLogsPerModel,
    int ModelBatchSize,
    int MaxCycleModels,
    int LockTimeoutSeconds,
    double MaxKellyAbs,
    double ReturnPipScale,
    double MaxReturnAbs);

public sealed class MLErgodicityConfigReader(MLErgodicityOptions options)
{
    private const string CK_PollHours = "MLErgodicity:PollIntervalHours";
    private const string CK_WindowDays = "MLErgodicity:WindowDays";
    private const string CK_MinSamples = "MLErgodicity:MinSamples";
    private const string CK_MaxLogsPerModel = "MLErgodicity:MaxLogsPerModel";
    private const string CK_ModelBatchSize = "MLErgodicity:ModelBatchSize";
    private const string CK_MaxCycleModels = "MLErgodicity:MaxCycleModels";
    private const string CK_LockTimeoutSeconds = "MLErgodicity:LockTimeoutSeconds";
    private const string CK_MaxKellyAbs = "MLErgodicity:MaxKellyAbs";
    private const string CK_ReturnPipScale = "MLErgodicity:ReturnPipScale";
    private const string CK_MaxReturnAbs = "MLErgodicity:MaxReturnAbs";

    public async Task<MLErgodicityRuntimeConfig> LoadAsync(
        DbContext readCtx,
        CancellationToken ct)
    {
        int minSamples = Math.Clamp(
            await GetConfigAsync(readCtx, CK_MinSamples, options.MinSamples, ct),
            2,
            10_000);
        int maxLogsPerModel = Math.Clamp(
            await GetConfigAsync(readCtx, CK_MaxLogsPerModel, options.MaxLogsPerModel, ct),
            minSamples,
            10_000);

        return new MLErgodicityRuntimeConfig(
            PollIntervalHours: Math.Clamp(
                await GetConfigAsync(readCtx, CK_PollHours, options.PollIntervalHours, ct),
                1,
                168),
            WindowDays: Math.Clamp(
                await GetConfigAsync(readCtx, CK_WindowDays, options.WindowDays, ct),
                1,
                365),
            MinSamples: minSamples,
            MaxLogsPerModel: maxLogsPerModel,
            ModelBatchSize: Math.Clamp(
                await GetConfigAsync(readCtx, CK_ModelBatchSize, options.ModelBatchSize, ct),
                1,
                10_000),
            MaxCycleModels: Math.Clamp(
                await GetConfigAsync(readCtx, CK_MaxCycleModels, options.MaxCycleModels, ct),
                1,
                250_000),
            LockTimeoutSeconds: Math.Clamp(
                await GetConfigAsync(readCtx, CK_LockTimeoutSeconds, options.LockTimeoutSeconds, ct),
                0,
                300),
            MaxKellyAbs: Math.Clamp(
                await GetConfigAsync(readCtx, CK_MaxKellyAbs, options.MaxKellyAbs, ct),
                0.01,
                100.0),
            ReturnPipScale: Math.Clamp(
                await GetConfigAsync(readCtx, CK_ReturnPipScale, options.ReturnPipScale, ct),
                0.01,
                100_000.0),
            MaxReturnAbs: Math.Clamp(
                await GetConfigAsync(readCtx, CK_MaxReturnAbs, options.MaxReturnAbs, ct),
                0.000001,
                0.999999));
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
        else if (targetType == typeof(int) &&
                 int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            parsed = intValue;
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
}
