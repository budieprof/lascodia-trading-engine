using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for exporting live ML metrics into EngineConfig.</summary>
public sealed class MLMetricsExportOptions : ConfigurationOption<MLMetricsExportOptions>
{
    /// <summary>Whether the worker is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Additional startup delay after the shared worker startup sequencer.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often the worker recomputes metric snapshots.</summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>Look-back window for predictions and resolved outcomes.</summary>
    public int WindowDays { get; set; } = 14;

    /// <summary>Minimum resolved outcomes before the snapshot is considered healthy.</summary>
    public int MinResolvedSamples { get; set; } = 1;

    /// <summary>Maximum active models exported in one cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 10_000;

    /// <summary>Maximum recent prediction rows loaded for a single model.</summary>
    public int MaxPredictionLogsPerModel { get; set; } = 50_000;

    /// <summary>When true, also writes legacy MLMetrics:{Symbol}:{Timeframe}:* dashboard aliases.</summary>
    public bool WriteLegacySymbolTimeframeAliases { get; set; } = true;

    /// <summary>Timeout for acquiring the singleton cycle distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Relational database command timeout applied to each cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;
}
