namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>Engine degradation states when subsystems are unavailable.</summary>
public enum DegradationMode
{
    /// <summary>All subsystems operational.</summary>
    Normal = 0,
    /// <summary>ML scoring unavailable — rule-based signals only, lot size reduced 50%.</summary>
    MLDegraded = 1,
    /// <summary>Event bus unavailable — in-memory event dispatch, single-instance only.</summary>
    EventBusDegraded = 2,
    /// <summary>Read database unavailable — serving from cache, new signal generation blocked.</summary>
    ReadDbDegraded = 3,
    /// <summary>Emergency halt — all trading suspended, positions being flattened.</summary>
    EmergencyHalt = 4,
    /// <summary>All EA instances disconnected — no market data available, signal generation blocked.</summary>
    DataUnavailable = 5
}
