namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Thrown when candle data fails quality validation (gaps, staleness, insufficient bars).
/// This is a recoverable condition — the optimization run should be deferred back to the
/// queue rather than marked as permanently failed, since new candle data may resolve the issue.
/// </summary>
internal sealed class DataQualityException : Exception
{
    public DataQualityException(string message) : base(message) { }
    public DataQualityException(string message, Exception innerException) : base(message, innerException) { }
}
