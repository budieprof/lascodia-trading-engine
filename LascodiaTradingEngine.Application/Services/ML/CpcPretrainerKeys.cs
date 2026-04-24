using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

public static class CpcPretrainerKeys
{
    public const string KeyPrefix = "MLCpcPretrainer";

    public static string BuildCandidateLockKey(CpcPairCandidate candidate, MLCpcRuntimeConfig config)
        => $"{KeyPrefix}:{EscapeKeyComponent(candidate.Symbol)}:{candidate.Timeframe}:{candidate.Regime?.ToString() ?? "global"}:{config.EncoderType}";

    public static string BuildConsecutiveFailureAlertDedupeKey(
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config)
    {
        var regimeLabel = candidate.Regime?.ToString() ?? "global";
        return $"{KeyPrefix}:{EscapeKeyComponent(candidate.Symbol)}:{candidate.Timeframe}:{regimeLabel}:{config.EncoderType}";
    }

    public static string BuildStaleEncoderAlertDedupeKey(
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config)
    {
        var regimeLabel = candidate.Regime?.ToString() ?? "global";
        return $"{KeyPrefix}:StaleEncoder:{EscapeKeyComponent(candidate.Symbol)}:{candidate.Timeframe}:{regimeLabel}:{config.EncoderType}";
    }

    public static string BuildConfigurationDriftAlertDedupeKey(
        string kind,
        CpcEncoderType encoderType)
        => $"{KeyPrefix}:ConfigurationDrift:{kind}:{encoderType}";

    public static string EscapeKeyComponent(string value)
        // ':' is our lock-key / dedupe-key separator. '/' appears in crypto symbols and
        // would otherwise visually collide with URL path segments when logs are rendered.
        => value.Replace(':', '_').Replace('/', '_');
}
