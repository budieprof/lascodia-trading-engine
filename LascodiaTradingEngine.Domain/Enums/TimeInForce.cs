namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>Order time-in-force policy governing how long an order remains active.</summary>
public enum TimeInForce
{
    /// <summary>Good 'Til Cancelled — remains active until explicitly cancelled or filled.</summary>
    GTC = 0,
    /// <summary>Immediate or Cancel — fill whatever is available immediately, cancel remainder.</summary>
    IOC = 1,
    /// <summary>Fill or Kill — fill the entire quantity immediately or cancel the whole order.</summary>
    FOK = 2,
    /// <summary>Good 'Til Date — remains active until a specified expiry date/time.</summary>
    GTD = 3
}
