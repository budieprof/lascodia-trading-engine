namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>High-impact operations that require four-eyes approval before execution.</summary>
public enum ApprovalOperationType
{
    StrategyActivation    = 0,
    ModelPromotion        = 1,
    RiskProfileChange     = 2,
    ConfigChange          = 3,
    EmergencyFlatten      = 4,
    RiskLimitLoosening    = 5
}
