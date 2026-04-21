using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Hot-reloadable runtime configuration for <c>CpcPretrainerWorker</c>. Mirrors
/// <see cref="MLCpcOptions"/> but values are resolved at runtime from the
/// <see cref="EngineConfig"/> table (falling back to options defaults), so operators
/// can tune the worker without a redeploy.
/// </summary>
public sealed record MLCpcRuntimeConfig(
    int     PollSeconds,
    int     RetrainIntervalHours,
    int     MaxPairsPerCycle,
    int     EmbeddingDim,
    int     PredictionSteps,
    int     SequenceLength,
    int     SequenceStride,
    int     MaxSequences,
    int     TrainingCandles,
    int     MinCandles,
    double  MinImprovement,
    double  MaxAcceptableLoss,
    double  ValidationSplit,
    int     MinValidationSequences,
    double  MaxValidationLoss,
    bool    Enabled,
    bool    SystemicPauseActive,
    int     ConsecutiveFailAlertThreshold,
    bool    TrainPerRegime,
    int     MinCandlesPerRegime,
    CpcEncoderType EncoderType);

public sealed class MLCpcConfigReader(MLCpcOptions options)
{
    private const string CK_PollSecs                      = "MLCpc:PollIntervalSeconds";
    private const string CK_RetrainIntervalHours          = "MLCpc:RetrainIntervalHours";
    private const string CK_MaxPairsPerCycle              = "MLCpc:MaxPairsPerCycle";
    private const string CK_EmbeddingDim                  = "MLCpc:EmbeddingDim";
    private const string CK_PredictionSteps              = "MLCpc:PredictionSteps";
    private const string CK_SequenceLength                = "MLCpc:SequenceLength";
    private const string CK_SequenceStride                = "MLCpc:SequenceStride";
    private const string CK_MaxSequences                  = "MLCpc:MaxSequences";
    private const string CK_TrainingCandles               = "MLCpc:TrainingCandles";
    private const string CK_MinCandles                    = "MLCpc:MinCandles";
    private const string CK_MinImprovement                = "MLCpc:MinImprovement";
    private const string CK_MaxAcceptableLoss             = "MLCpc:MaxAcceptableLoss";
    private const string CK_ValidationSplit               = "MLCpc:ValidationSplit";
    private const string CK_MinValidationSequences        = "MLCpc:MinValidationSequences";
    private const string CK_MaxValidationLoss             = "MLCpc:MaxValidationLoss";
    private const string CK_Enabled                       = "MLCpc:Enabled";
    private const string CK_ConsecutiveFailAlertThreshold = "MLCpc:ConsecutiveFailAlertThreshold";
    private const string CK_TrainPerRegime                = "MLCpc:TrainPerRegime";
    private const string CK_MinCandlesPerRegime           = "MLCpc:MinCandlesPerRegime";
    private const string CK_EncoderType                   = "MLCpc:EncoderType";
    private const string CK_SystemicPause                 = "MLTraining:SystemicPauseActive";

    public async Task<MLCpcRuntimeConfig> LoadAsync(DbContext ctx, CancellationToken ct)
    {
        int pollSecs = Math.Clamp(
            await GetConfigAsync(ctx, CK_PollSecs, options.PollIntervalSeconds, ct),
            300, 86_400);
        int retrainHrs = Math.Max(
            1,
            await GetConfigAsync(ctx, CK_RetrainIntervalHours, options.RetrainIntervalHours, ct));
        int maxPairs = Math.Max(
            1,
            await GetConfigAsync(ctx, CK_MaxPairsPerCycle, options.MaxPairsPerCycle, ct));
        int embedDim = Math.Clamp(
            await GetConfigAsync(ctx, CK_EmbeddingDim, options.EmbeddingDim, ct),
            4, 128);
        int predSteps = Math.Clamp(
            await GetConfigAsync(ctx, CK_PredictionSteps, options.PredictionSteps, ct),
            1, 10);
        int seqLen = Math.Max(
            predSteps + 2,
            await GetConfigAsync(ctx, CK_SequenceLength, options.SequenceLength, ct));
        int seqStride = Math.Max(
            1,
            await GetConfigAsync(ctx, CK_SequenceStride, options.SequenceStride, ct));
        int maxSeqs = Math.Max(
            1,
            await GetConfigAsync(ctx, CK_MaxSequences, options.MaxSequences, ct));
        int minCandles = Math.Max(
            seqLen,
            await GetConfigAsync(ctx, CK_MinCandles, options.MinCandles, ct));
        int trainCandles = Math.Max(
            minCandles,
            await GetConfigAsync(ctx, CK_TrainingCandles, options.TrainingCandles, ct));
        double minImprovement = Math.Clamp(
            await GetConfigAsync(ctx, CK_MinImprovement, options.MinImprovement, ct),
            0.0, 0.5);
        double maxLoss = await GetConfigAsync(ctx, CK_MaxAcceptableLoss, options.MaxAcceptableLoss, ct);
        if (!double.IsFinite(maxLoss) || maxLoss <= 0.0) maxLoss = options.MaxAcceptableLoss;
        double validationSplit = Math.Clamp(
            await GetConfigAsync(ctx, CK_ValidationSplit, options.ValidationSplit, ct),
            0.05, 0.5);
        int minValidationSequences = Math.Max(
            1,
            await GetConfigAsync(ctx, CK_MinValidationSequences, options.MinValidationSequences, ct));
        double maxValidationLoss = await GetConfigAsync(ctx, CK_MaxValidationLoss, options.MaxValidationLoss, ct);
        if (!double.IsFinite(maxValidationLoss) || maxValidationLoss <= 0.0)
            maxValidationLoss = options.MaxValidationLoss;
        bool enabled = await GetConfigAsync(ctx, CK_Enabled, options.Enabled, ct);
        bool systemicPause = await GetConfigAsync(ctx, CK_SystemicPause, false, ct);
        int failThresh = Math.Max(
            1,
            await GetConfigAsync(ctx, CK_ConsecutiveFailAlertThreshold, options.ConsecutiveFailAlertThreshold, ct));
        bool trainPerRegime = await GetConfigAsync(ctx, CK_TrainPerRegime, options.TrainPerRegime, ct);
        int minPerRegime = Math.Max(
            seqLen,
            await GetConfigAsync(ctx, CK_MinCandlesPerRegime, options.MinCandlesPerRegime, ct));

        var encoderTypeName = await GetConfigAsync(ctx, CK_EncoderType, options.EncoderType.ToString(), ct);
        var encoderType = Enum.TryParse<CpcEncoderType>(encoderTypeName, ignoreCase: true, out var parsedType)
            ? parsedType
            : options.EncoderType;

        return new MLCpcRuntimeConfig(
            PollSeconds: pollSecs,
            RetrainIntervalHours: retrainHrs,
            MaxPairsPerCycle: maxPairs,
            EmbeddingDim: embedDim,
            PredictionSteps: predSteps,
            SequenceLength: seqLen,
            SequenceStride: seqStride,
            MaxSequences: maxSeqs,
            TrainingCandles: trainCandles,
            MinCandles: minCandles,
            MinImprovement: minImprovement,
            MaxAcceptableLoss: maxLoss,
            ValidationSplit: validationSplit,
            MinValidationSequences: minValidationSequences,
            MaxValidationLoss: maxValidationLoss,
            Enabled: enabled,
            SystemicPauseActive: systemicPause,
            ConsecutiveFailAlertThreshold: failThresh,
            TrainPerRegime: trainPerRegime,
            MinCandlesPerRegime: minPerRegime,
            EncoderType: encoderType);
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
            parsed = value;
        else if (targetType == typeof(bool) && bool.TryParse(value, out var b))
            parsed = b;
        else if (targetType == typeof(int) &&
                 int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            parsed = i;
        else if (targetType == typeof(long) &&
                 long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            parsed = l;
        else if (targetType == typeof(double) &&
                 double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            parsed = d;
        else if (targetType == typeof(decimal) &&
                 decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var m))
            parsed = m;

        if (parsed is T typed)
        {
            result = typed;
            return true;
        }

        result = default!;
        return false;
    }
}
