namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

public record Tick(string Symbol, decimal Bid, decimal Ask, DateTime Timestamp);

public interface IBrokerDataFeed
{
    /// <summary>Subscribe to live price ticks for the given symbols.</summary>
    Task SubscribeAsync(IEnumerable<string> symbols, Func<Tick, Task> onTick, CancellationToken cancellationToken);

    /// <summary>Fetch historical candles from the broker.</summary>
    Task<IReadOnlyList<BrokerCandle>> GetCandlesAsync(
        string symbol, string timeframe, DateTime from, DateTime to, CancellationToken cancellationToken);
}

public record BrokerCandle(
    string Symbol,
    string Timeframe,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    DateTime Timestamp,
    bool IsClosed);
