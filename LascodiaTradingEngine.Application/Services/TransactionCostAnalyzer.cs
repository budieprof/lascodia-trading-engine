using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Decomposes execution cost using implementation-shortfall methodology.
/// Breaks down total cost into delay, market impact, spread, and commission components.
/// Also provides pre-trade cost estimation for order sizing decisions.
/// </summary>
[RegisterService]
public class TransactionCostAnalyzer : ITransactionCostAnalyzer
{
    private readonly ILogger<TransactionCostAnalyzer> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILivePriceCache _livePriceCache;

    /// <summary>Number of recent fills to use for slippage estimation.</summary>
    private const int SlippageLookbackCount = 50;

    /// <summary>EngineConfig key for default commission per lot when no symbol-specific data exists.</summary>
    private const string DefaultCommissionConfigKey = "TCA:DefaultCommissionPerLot";

    /// <summary>Default linear market impact coefficient (cost per unit of participation rate).</summary>
    private const decimal DefaultImpactCoefficient = 0.1m;

    public TransactionCostAnalyzer(
        ILogger<TransactionCostAnalyzer> logger,
        IServiceScopeFactory scopeFactory,
        ILivePriceCache livePriceCache)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _livePriceCache = livePriceCache;
    }

    public async Task<TransactionCostAnalysis> AnalyzeAsync(
        Order filledOrder,
        TradeSignal? originatingSignal,
        CancellationToken cancellationToken)
    {
        if (filledOrder.FilledPrice is null)
            throw new InvalidOperationException($"Order {filledOrder.Id} has no fill price");

        var fillPrice = filledOrder.FilledPrice.Value;

        // Arrival price = mid-price at signal creation (or order price as fallback)
        decimal arrivalPrice = originatingSignal?.EntryPrice ?? filledOrder.Price;

        // Submission price = order price (mid at time of submission to broker)
        decimal submissionPrice = filledOrder.Price;

        // Direction multiplier: Buy = +1 (higher fill = cost), Sell = -1 (lower fill = cost)
        decimal directionSign = filledOrder.OrderType == Domain.Enums.OrderType.Buy ? 1m : -1m;

        // Implementation shortfall: total execution cost vs decision price
        decimal implementationShortfall = directionSign * (fillPrice - arrivalPrice);

        // Delay cost: price drift from signal creation to order submission
        decimal delayCost = directionSign * (submissionPrice - arrivalPrice);

        // Market impact cost: price drift from submission to fill
        decimal marketImpactCost = directionSign * (fillPrice - submissionPrice);

        // Spread cost: half the spread at execution time (estimated from bid-ask if available)
        // Approximate as |fill - order price| when actual spread unavailable
        decimal spreadCost = Math.Abs(fillPrice - submissionPrice) / 2m;

        // Commission cost: read from EngineConfig (configurable default per lot)
        decimal commissionPerLot = await GetCommissionPerLotAsync(filledOrder.Symbol, cancellationToken);
        decimal commissionCost = commissionPerLot * filledOrder.Quantity;

        // Total cost = delay + market impact + commission.
        // Spread is a component of market impact, not additive to implementation shortfall.
        decimal totalCost = delayCost + marketImpactCost + commissionCost;

        // Total cost in basis points
        decimal notional   = filledOrder.Quantity * fillPrice;
        decimal totalBps   = notional > 0 ? totalCost / notional * 10_000m : 0;

        // Timing metrics
        long signalToFillMs = 0;
        long submitToFillMs = 0;

        if (originatingSignal is not null && filledOrder.FilledAt.HasValue)
        {
            signalToFillMs = (long)(filledOrder.FilledAt.Value - originatingSignal.GeneratedAt).TotalMilliseconds;
        }

        if (filledOrder.FilledAt.HasValue)
        {
            submitToFillMs = (long)(filledOrder.FilledAt.Value - filledOrder.CreatedAt).TotalMilliseconds;
        }

        var tca = new TransactionCostAnalysis
        {
            OrderId                  = filledOrder.Id,
            TradeSignalId            = originatingSignal?.Id,
            Symbol                   = filledOrder.Symbol,
            ArrivalPrice             = arrivalPrice,
            FillPrice                = fillPrice,
            SubmissionPrice          = submissionPrice,
            ImplementationShortfall  = implementationShortfall,
            DelayCost                = delayCost,
            MarketImpactCost         = marketImpactCost,
            SpreadCost               = spreadCost,
            CommissionCost           = commissionCost,
            TotalCost                = totalCost,
            TotalCostBps             = totalBps,
            Quantity                 = filledOrder.Quantity,
            SignalToFillMs           = signalToFillMs,
            SubmissionToFillMs       = submitToFillMs,
            AnalyzedAt               = DateTime.UtcNow
        };

        _logger.LogDebug(
            "TCA: order {OrderId} {Symbol} — shortfall={IS:F5}, delay={Delay:F5}, impact={Impact:F5}, spread={Spread:F5}, total={Total:F2}bps",
            filledOrder.Id, filledOrder.Symbol,
            implementationShortfall, delayCost, marketImpactCost, spreadCost, totalBps);

        return tca;
    }

    /// <summary>
    /// Estimates execution cost BEFORE placing the order, using historical spread,
    /// slippage, and estimated market impact based on order size.
    /// </summary>
    public async Task<TransactionCostEstimate> EstimatePreTradeAsync(
        string symbol, decimal quantity, decimal entryPrice, CancellationToken ct)
    {
        // Spread: from live price cache (bid-ask difference)
        decimal estimatedSpread = 0m;
        var livePrice = _livePriceCache.Get(symbol);
        if (livePrice.HasValue)
        {
            estimatedSpread = livePrice.Value.Ask - livePrice.Value.Bid;
        }
        else
        {
            // Fallback: use SpreadProfile mean
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var profileSpread = await readContext.GetDbContext()
                    .Set<SpreadProfile>()
                    .Where(sp => sp.Symbol == symbol && !sp.IsDeleted)
                    .OrderByDescending(sp => sp.ComputedAt)
                    .Select(sp => sp.SpreadMean)
                    .FirstOrDefaultAsync(ct);
                estimatedSpread = profileSpread;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TCA pre-trade: failed to load spread profile for {Symbol}", symbol);
            }
        }

        // Slippage: average from recent ExecutionQualityLog entries
        decimal estimatedSlippage = 0m;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var recentSlippages = await readContext.GetDbContext()
                .Set<ExecutionQualityLog>()
                .Where(e => e.Symbol == symbol && !e.IsDeleted)
                .OrderByDescending(e => e.RecordedAt)
                .Take(SlippageLookbackCount)
                .Select(e => Math.Abs(e.SlippagePips))
                .ToListAsync(ct);

            if (recentSlippages.Count > 0)
                estimatedSlippage = recentSlippages.Average();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TCA pre-trade: failed to load slippage data for {Symbol}", symbol);
        }

        // Market impact: simplified linear model = quantity / avgDailyVolume * impactCoefficient
        decimal estimatedMarketImpact = 0m;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var cutoff = DateTime.UtcNow.AddDays(-7);
            var dailyVolumes = await readContext.GetDbContext()
                .Set<TickRecord>()
                .Where(t => t.Symbol == symbol && t.TickTimestamp >= cutoff && !t.IsDeleted)
                .GroupBy(t => t.TickTimestamp.Date)
                .Select(g => g.Sum(t => t.TickVolume))
                .ToListAsync(ct);

            if (dailyVolumes.Count > 0)
            {
                decimal avgDailyVolume = dailyVolumes.Average(v => (decimal)v);
                if (avgDailyVolume > 0)
                {
                    decimal participationRate = quantity / avgDailyVolume;
                    estimatedMarketImpact = participationRate * DefaultImpactCoefficient * entryPrice;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TCA pre-trade: failed to estimate market impact for {Symbol}", symbol);
        }

        // Commission
        decimal commissionPerLot = await GetCommissionPerLotAsync(symbol, ct);
        decimal estimatedCommission = commissionPerLot * quantity;

        // Spread cost in price terms (half-spread is the cost to cross)
        decimal spreadCost = estimatedSpread / 2m * quantity;

        decimal totalEstimatedCost = spreadCost + (estimatedSlippage * quantity) + estimatedMarketImpact + estimatedCommission;
        decimal totalBps = (entryPrice * quantity) > 0
            ? totalEstimatedCost / (entryPrice * quantity) * 10_000m
            : 0m;

        _logger.LogDebug(
            "TCA pre-trade: {Symbol} qty={Qty} — spread={Spread:F5}, slippage={Slip:F3}pips, impact={Impact:F5}, commission={Comm:F4}, total={Total:F2}bps",
            symbol, quantity, estimatedSpread, estimatedSlippage, estimatedMarketImpact, estimatedCommission, totalBps);

        return new TransactionCostEstimate(
            Symbol: symbol,
            Quantity: quantity,
            EstimatedSpreadCost: spreadCost,
            EstimatedSlippagePips: estimatedSlippage,
            EstimatedMarketImpact: estimatedMarketImpact,
            EstimatedCommission: estimatedCommission,
            TotalEstimatedCost: totalEstimatedCost,
            TotalEstimatedBps: totalBps,
            EstimatedAt: DateTime.UtcNow);
    }

    /// <summary>
    /// Reads the commission per lot for a symbol from EngineConfig.
    /// Falls back to a hardcoded default of 7.0 per lot if no config is found.
    /// </summary>
    private async Task<decimal> GetCommissionPerLotAsync(string symbol, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

            // Try symbol-specific config first, then global default
            var symbolKey = $"TCA:CommissionPerLot:{symbol}";
            var config = await readContext.GetDbContext()
                .Set<EngineConfig>()
                .Where(c => (c.Key == symbolKey || c.Key == DefaultCommissionConfigKey) && !c.IsDeleted)
                .OrderByDescending(c => c.Key == symbolKey ? 1 : 0) // Prefer symbol-specific
                .FirstOrDefaultAsync(ct);

            if (config is not null && decimal.TryParse(config.Value, out var commission))
                return commission;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TCA: failed to read commission config for {Symbol}, using default", symbol);
        }

        // Fallback default: 7.0 per lot (typical FX commission)
        return 7.0m;
    }
}
