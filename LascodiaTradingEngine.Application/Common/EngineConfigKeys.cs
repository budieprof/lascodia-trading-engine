using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Common;

/// <summary>
/// Central registry of all valid <see cref="Domain.Entities.EngineConfig"/> key prefixes
/// used throughout the trading engine. Workers store dynamic configuration using keys of the
/// form <c>{Prefix}:{Symbol}:{Timeframe}:SettingName</c>; this class validates that the
/// prefix portion is known and provides safe formatting helpers.
/// </summary>
public static class EngineConfigKeys
{
    /// <summary>
    /// All recognised EngineConfig key prefixes. Keys that do not start with one of these
    /// prefixes are considered unknown and will trigger a warning (not an error).
    /// </summary>
    public static readonly HashSet<string> ValidPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MLTraining:",
        "MLDrift:",
        "MLCovariate:",
        "MLCusum:",
        "MLMultiScaleDrift:",
        "MLShadow:",
        "MLRetirement:",
        "MLOutcome:",
        "MLRunHealth:",
        "MLFreshness:",
        "MLOnline:",
        "MLDistillation:",
        "MLSoup:",
        "MLMaml:",
        "MLDegradation:",
        "MLFeatureStale:",
        "MLCalibration:",
        "MLMetrics:",
        "MLInference:",
        "MLWarmup:",
        "MLResourceGuard:",
        "MLDeadLetter:",
        "MLAlertFatigue:",
        "MLDriftAgreement:",
        "MLApproval:",
        "MLCooldown:",
        "MLKelly:",
        "MLScoring:",
        "MLModel:",
        "MLTrainingCost:",
        "DeadLetter:",
        "RegimeDetector:",
        "WorkerGroups:",
        "RiskCheckerOptions:",
        "EngineConfig:",
    };

    /// <summary>
    /// Checks whether <paramref name="key"/> starts with a known prefix. If the prefix is
    /// unrecognised, a warning is logged (but the call does not throw). This is intended
    /// as a development-time guard to catch typos and undocumented config keys.
    /// </summary>
    /// <param name="key">The full EngineConfig key to validate.</param>
    /// <param name="logger">Optional logger; when null the warning is silently swallowed.</param>
    /// <returns><c>true</c> if the key starts with a known prefix; <c>false</c> otherwise.</returns>
    public static bool ValidateKeyPrefix(string key, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            logger?.LogWarning("EngineConfigKeys: empty or null key supplied.");
            return false;
        }

        foreach (var prefix in ValidPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        logger?.LogWarning(
            "EngineConfigKeys: key '{Key}' does not match any known prefix. " +
            "If this is intentional, add the prefix to EngineConfigKeys.ValidPrefixes.",
            key);

        return false;
    }

    /// <summary>
    /// Safely formats a key template by substituting <c>{0}</c> with the symbol and
    /// <c>{1}</c> with the timeframe string representation.
    /// </summary>
    /// <param name="template">
    /// A key template containing positional placeholders, e.g.
    /// <c>"MLDrift:{0}:{1}:ConsecutiveFailures"</c>.
    /// </param>
    /// <param name="symbol">The trading symbol (e.g. "EURUSD").</param>
    /// <param name="timeframe">The candle timeframe.</param>
    /// <returns>The formatted key with placeholders replaced.</returns>
    public static string FormatKey(string template, string symbol, Timeframe timeframe)
    {
        return string.Format(template, symbol, timeframe.ToString());
    }
}
