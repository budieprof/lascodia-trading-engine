using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.SignalFilters;

/// <summary>
/// Blocks all new trade signals when the account's peak-to-trough drawdown exceeds
/// a configurable threshold (default 20%). Reads the threshold from EngineConfig
/// key "RiskManagement:MaxDrawdownHaltPct".
/// </summary>
public sealed class MaxDrawdownTradingHalt
{
    private readonly ILogger<MaxDrawdownTradingHalt> _logger;

    public MaxDrawdownTradingHalt(ILogger<MaxDrawdownTradingHalt> logger)
    {
        _logger = logger;
    }

    public async Task<(bool Allowed, string? BlockReason)> CheckAsync(
        long tradingAccountId, DbContext db, CancellationToken ct)
    {
        var account = await db.Set<TradingAccount>()
            .AsNoTracking()
            .Where(a => a.Id == tradingAccountId && !a.IsDeleted)
            .Select(a => new { a.Balance, a.Equity })
            .FirstOrDefaultAsync(ct);

        if (account is null)
            return (true, null); // No account = pass (validated elsewhere)

        // Peak equity from the latest drawdown snapshot
        var peakEquity = await db.Set<DrawdownSnapshot>()
            .Where(s => !s.IsDeleted)
            .OrderByDescending(s => s.PeakEquity)
            .Select(s => (decimal?)s.PeakEquity)
            .FirstOrDefaultAsync(ct);

        if (peakEquity is null or <= 0)
            return (true, null); // No peak recorded yet

        decimal currentEquity = account.Equity > 0 ? account.Equity : account.Balance;
        decimal drawdownPct = (peakEquity.Value - currentEquity) / peakEquity.Value;

        // Load threshold from config
        decimal threshold = 0.20m;
        var config = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == "RiskManagement:MaxDrawdownHaltPct", ct);
        if (config is not null && decimal.TryParse(config.Value, out var parsed) && parsed > 0)
            threshold = parsed;

        if (drawdownPct >= threshold)
        {
            _logger.LogWarning(
                "MaxDrawdownTradingHalt: account {AccountId} drawdown {DD:P1} exceeds threshold {Threshold:P1} — blocking signals",
                tradingAccountId, drawdownPct, threshold);
            return (false, $"Max drawdown {drawdownPct:P1} exceeds halt threshold {threshold:P1}");
        }

        return (true, null);
    }
}
