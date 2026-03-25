namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Provides access to the most recently computed rolling correlation matrix.
/// Keys are alphabetically-ordered symbol pairs: "EURUSD|GBPUSD" → 0.85.
/// </summary>
public interface ICorrelationMatrixProvider
{
    IReadOnlyDictionary<string, decimal> GetCorrelations();
    DateTime LastComputedAtUtc { get; }
}
