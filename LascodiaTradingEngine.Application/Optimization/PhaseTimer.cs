using System.Diagnostics;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Manages a structured log scope + stopwatch + metric recording for a single
/// optimization phase. Disposing the timer records the phase duration metric
/// and disposes the log scope, preventing scope leaks if an exception fires
/// between phase start and manual dispose.
/// </summary>
internal sealed class PhaseTimer : IDisposable
{
    private readonly Stopwatch _sw;
    private readonly IDisposable? _logScope;
    private readonly TradingMetrics _metrics;
    private readonly string _phaseName;
    private bool _disposed;

    private PhaseTimer(Stopwatch sw, IDisposable? logScope, TradingMetrics metrics, string phaseName)
    {
        _sw = sw;
        _logScope = logScope;
        _metrics = metrics;
        _phaseName = phaseName;
    }

    /// <summary>
    /// Starts a new phase timer with a structured log scope containing the
    /// run ID, strategy ID, and phase name.
    /// </summary>
    internal static PhaseTimer Start(
        ILogger logger, TradingMetrics metrics,
        long runId, long strategyId, string phaseName)
    {
        var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["OptimizationRunId"] = runId,
            ["StrategyId"] = strategyId,
            ["Phase"] = phaseName,
        });
        return new PhaseTimer(Stopwatch.StartNew(), logScope, metrics, phaseName);
    }

    /// <summary>Records the elapsed time and restarts the timer for a new phase.</summary>
    internal PhaseTimer NextPhase(ILogger logger, long runId, long strategyId, string nextPhaseName)
    {
        Dispose();
        return Start(logger, _metrics, runId, strategyId, nextPhaseName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sw.Stop();
        _metrics.OptimizationPhaseDurationMs.Record(_sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("phase", _phaseName));
        _logScope?.Dispose();
    }
}
