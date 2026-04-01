namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>Types of anomalies detected in incoming market data from EA instances.</summary>
public enum MarketDataAnomalyType
{
    /// <summary>Tick price deviates beyond threshold from last valid price.</summary>
    PriceSpike = 0,
    /// <summary>Bid/ask unchanged for longer than expected during active session.</summary>
    StaleQuote = 1,
    /// <summary>Bid exceeds ask — invalid quote.</summary>
    InvertedSpread = 2,
    /// <summary>Volume exceeds threshold multiple of recent average.</summary>
    VolumeAnomaly = 3,
    /// <summary>Tick timestamp is earlier than the previous tick — clock skew.</summary>
    TimestampRegression = 4,
    /// <summary>OHLC relationship violated (High &lt; Open or Low &gt; Close).</summary>
    InvalidOhlc = 5
}
