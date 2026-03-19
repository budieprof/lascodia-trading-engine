namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface ILivePriceCache
{
    void Update(string symbol, decimal bid, decimal ask, DateTime timestamp);
    (decimal Bid, decimal Ask, DateTime Timestamp)? Get(string symbol);
    IReadOnlyDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> GetAll();
}
