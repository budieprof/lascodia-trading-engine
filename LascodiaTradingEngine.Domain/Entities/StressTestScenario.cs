using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Defines a stress test scenario — either a historical replay (e.g. SNB de-peg),
/// a hypothetical shock, or a reverse stress test that finds the minimum shock
/// causing a specified loss level. Scenarios are run by <see cref="Workers.StressTestWorker"/>.
/// </summary>
public class StressTestScenario : Entity<long>
{
    /// <summary>Human-readable scenario name (e.g. "SNB De-Peg Jan 2015").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Scenario classification: Historical, Hypothetical, or ReverseStress.</summary>
    public StressScenarioType ScenarioType { get; set; }

    /// <summary>
    /// JSON-serialised shock definition. Structure depends on ScenarioType:
    /// Historical: { "symbol": "EURCHF", "dateFrom": "2015-01-15", "dateTo": "2015-01-16" }
    /// Hypothetical: { "shocks": [{ "symbol": "EURUSD", "pctChange": -5.0 }], "spreadMultiplier": 10 }
    /// ReverseStress: { "targetLossPct": 25.0, "searchSymbols": ["EURUSD", "GBPUSD"] }
    /// </summary>
    public string ShockDefinitionJson { get; set; } = "{}";

    /// <summary>Whether this scenario is included in the weekly automated stress test run.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Optional description with context (e.g. regulatory requirement, known risk).</summary>
    public string? Description { get; set; }

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
