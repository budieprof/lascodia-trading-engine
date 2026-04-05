namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Specifies the data type of an engine configuration value stored in <c>EngineConfig</c>.
/// </summary>
public enum ConfigDataType
{
    /// <summary>Free-text string value.</summary>
    String = 0,

    /// <summary>Integer numeric value.</summary>
    Int = 1,

    /// <summary>Decimal (floating-point) numeric value.</summary>
    Decimal = 2,

    /// <summary>Boolean true/false value.</summary>
    Bool = 3,

    /// <summary>Structured JSON object or array.</summary>
    Json = 4
}
