namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Controls the engine's trading posture during drawdown recovery.
/// </summary>
public enum RecoveryMode
{
    /// <summary>Full trading at normal position sizes and risk limits.</summary>
    Normal = 0,

    /// <summary>Reduced position sizes and tighter risk limits to recover from drawdown.</summary>
    Reduced = 1,

    /// <summary>All new trading halted until drawdown recovery criteria are met.</summary>
    Halted = 2
}
