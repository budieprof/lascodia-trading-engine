using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

/// <summary>
/// Stub adapter for OANDA v20 order management REST API.
/// Replace method bodies with real HTTP calls.
/// Credentials are loaded from the active Broker/TradingAccount DB records.
/// </summary>
[RegisterService]
public class OandaOrderExecutor : IBrokerOrderExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OandaOrderExecutor> _logger;

    public OandaOrderExecutor(IServiceScopeFactory scopeFactory, ILogger<OandaOrderExecutor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task<BrokerOrderResult> SubmitOrderAsync(Order order, CancellationToken cancellationToken)
    {
        var accountId = await GetAccountIdAsync(cancellationToken);

        // TODO: POST /v3/accounts/{accountId}/orders
        _logger.LogWarning(
            "OandaOrderExecutor.SubmitOrderAsync is not yet implemented — simulating fill (account={AccountId})",
            accountId);

        var fakeResult = new BrokerOrderResult(
            Success: true,
            BrokerOrderId: $"OANDA-{Guid.NewGuid():N}",
            FilledPrice: order.Price > 0 ? order.Price : null,
            FilledQuantity: order.Quantity,
            ErrorMessage: null);

        return fakeResult;
    }

    public async Task<BrokerOrderResult> CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken)
    {
        var accountId = await GetAccountIdAsync(cancellationToken);

        // TODO: PUT /v3/accounts/{accountId}/orders/{orderId}/cancel
        _logger.LogWarning(
            "OandaOrderExecutor.CancelOrderAsync is not yet implemented (account={AccountId})",
            accountId);

        return new BrokerOrderResult(true, brokerOrderId, null, null, null);
    }

    public async Task<BrokerOrderResult> ModifyOrderAsync(
        string brokerOrderId, decimal? stopLoss, decimal? takeProfit, CancellationToken cancellationToken)
    {
        var accountId = await GetAccountIdAsync(cancellationToken);

        // TODO: PUT /v3/accounts/{accountId}/orders/{orderId}
        _logger.LogWarning(
            "OandaOrderExecutor.ModifyOrderAsync is not yet implemented (account={AccountId})",
            accountId);

        return new BrokerOrderResult(true, brokerOrderId, null, null, null);
    }

    public async Task<BrokerOrderResult> ClosePositionAsync(
        string brokerPositionId, decimal lots, CancellationToken cancellationToken)
    {
        var accountId = await GetAccountIdAsync(cancellationToken);

        // TODO: PUT /v3/accounts/{accountId}/positions/{instrument}/close
        _logger.LogWarning(
            "OandaOrderExecutor.ClosePositionAsync is not yet implemented (account={AccountId})",
            accountId);

        return new BrokerOrderResult(true, brokerPositionId, null, lots, null);
    }

    public async Task<BrokerAccountSummary?> GetAccountSummaryAsync(CancellationToken cancellationToken)
    {
        var accountId = await GetAccountIdAsync(cancellationToken);

        // TODO: GET /v3/accounts/{accountId}/summary
        _logger.LogWarning(
            "OandaOrderExecutor.GetAccountSummaryAsync is not yet implemented — returning stub values (account={AccountId})",
            accountId);

        // Return stub values until live broker call is implemented
        return new BrokerAccountSummary(
            Balance:         10_000m,
            Equity:          10_000m,
            MarginUsed:      0m,
            MarginAvailable: 10_000m);
    }

    private async Task<string> GetAccountIdAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db            = readContext.GetDbContext();

        var broker = await db.Set<Broker>()
            .FirstOrDefaultAsync(x => x.BrokerType == BrokerType.Oanda && x.IsActive && !x.IsDeleted, ct);

        if (broker == null)
            throw new InvalidOperationException("No active Oanda broker configured in database");

        var account = await db.Set<TradingAccount>()
            .FirstOrDefaultAsync(x => x.BrokerId == broker.Id && x.IsActive && !x.IsDeleted, ct);

        if (account == null)
            throw new InvalidOperationException("No active trading account found for the active Oanda broker");

        return account.AccountId;
    }
}
