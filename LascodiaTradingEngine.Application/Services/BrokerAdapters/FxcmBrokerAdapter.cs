using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

/// <summary>
/// FXCM REST API broker adapter scaffold.
///
/// FXCM API docs: https://fxcm.github.io/rest-api-docs/
/// Base URL: https://api-demo.fxcm.com (demo) / https://api.fxcm.com (live)
/// Auth: Bearer token via "Authorization: Bearer {accessToken}" header
///
/// Symbol mapping (OANDA → FXCM):
///   EURUSD → EUR/USD, GBPUSD → GBP/USD, USDJPY → USD/JPY
///   (FXCM uses slash-separated pair names)
///
/// To complete this adapter:
/// 1. Set up an FXCM demo account at https://www.fxcm.com/
/// 2. Generate an API token from the Trading Station
/// 3. Store BaseUrl + ApiKey in a Broker entity with BrokerType = Fxcm
/// 4. Implement the TODO methods below with real HTTP calls
/// </summary>
[RegisterKeyedService(typeof(IBrokerOrderExecutor), BrokerType.Fxcm)]
public sealed class FxcmOrderExecutor : IBrokerOrderExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FxcmOrderExecutor> _logger;

    public FxcmOrderExecutor(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<FxcmOrderExecutor> logger)
    {
        _scopeFactory      = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    public async Task<BrokerOrderResult> SubmitOrderAsync(Order order, CancellationToken cancellationToken)
    {
        var creds = await GetCredentialsAsync(cancellationToken);

        // TODO: POST /trading/open_trade
        // Request body (form-encoded):
        //   account_id  = creds.AccountId
        //   symbol      = ToFxcmSymbol(order.Symbol)  // e.g. "EUR/USD"
        //   is_buy      = order.OrderType == OrderType.Buy
        //   amount      = (int)(order.Quantity * 1000) // FXCM uses K-lots (1K = 1000 units)
        //   rate        = 0                            // 0 for market order
        //   at_market   = 0                            // market range in pips
        //   order_type  = "AtMarket"
        //   time_in_force = "GTC"
        //   stop        = order.StopLoss               // optional
        //   limit       = order.TakeProfit             // optional
        //
        // Response JSON:
        //   { "response": { "executed": true }, "data": { "orderId": "12345", "tradeId": "67890" } }
        //
        // Extract tradeId as BrokerOrderId, then call GET /trading/get_model?models=OpenPosition
        // to retrieve the fill price.

        _logger.LogWarning(
            "FxcmOrderExecutor.SubmitOrderAsync: not yet implemented — " +
            "account={AccountId} symbol={Symbol} baseUrl={BaseUrl}",
            creds.AccountId, order.Symbol, creds.BaseUrl);

        return new BrokerOrderResult(
            Success: false,
            BrokerOrderId: null,
            FilledPrice: null,
            FilledQuantity: null,
            ErrorMessage: "FXCM adapter not yet implemented");
    }

    public async Task<BrokerOrderResult> CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken)
    {
        var creds = await GetCredentialsAsync(cancellationToken);

        // TODO: DELETE /trading/delete_order
        // Request body (form-encoded):
        //   order_id = brokerOrderId

        _logger.LogWarning("FxcmOrderExecutor.CancelOrderAsync: not yet implemented");
        return new BrokerOrderResult(false, brokerOrderId, null, null, "FXCM adapter not yet implemented");
    }

    public async Task<BrokerOrderResult> ModifyOrderAsync(
        string brokerOrderId, decimal? stopLoss, decimal? takeProfit, CancellationToken cancellationToken)
    {
        var creds = await GetCredentialsAsync(cancellationToken);

        // TODO: POST /trading/change_trade_stop_limit
        // Request body (form-encoded):
        //   trade_id  = brokerOrderId
        //   is_stop   = true,  rate = stopLoss   (for SL change)
        //   is_stop   = false, rate = takeProfit  (for TP change — requires separate call)

        _logger.LogWarning("FxcmOrderExecutor.ModifyOrderAsync: not yet implemented");
        return new BrokerOrderResult(false, brokerOrderId, null, null, "FXCM adapter not yet implemented");
    }

    public async Task<BrokerOrderResult> ClosePositionAsync(
        string brokerPositionId, decimal lots, CancellationToken cancellationToken)
    {
        var creds = await GetCredentialsAsync(cancellationToken);

        // TODO: POST /trading/close_trade
        // Request body (form-encoded):
        //   trade_id = brokerPositionId
        //   amount   = (int)(lots * 1000)  // K-lots

        _logger.LogWarning("FxcmOrderExecutor.ClosePositionAsync: not yet implemented");
        return new BrokerOrderResult(false, brokerPositionId, null, null, "FXCM adapter not yet implemented");
    }

    public async Task<BrokerAccountSummary?> GetAccountSummaryAsync(CancellationToken cancellationToken)
    {
        var creds = await GetCredentialsAsync(cancellationToken);

        // TODO: GET /trading/get_model?models=Account
        // Response JSON contains:
        //   balance, equity, usableMargin (= marginAvailable), usedMargin

        _logger.LogWarning("FxcmOrderExecutor.GetAccountSummaryAsync: not yet implemented");
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<FxcmCredentials> GetCredentialsAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db           = readContext.GetDbContext();

        var broker = await db.Set<Broker>()
            .FirstOrDefaultAsync(x => x.BrokerType == BrokerType.Fxcm && !x.IsDeleted, ct)
            ?? throw new InvalidOperationException("No FXCM broker configured in database");

        var account = await db.Set<TradingAccount>()
            .FirstOrDefaultAsync(x => x.BrokerId == broker.Id && x.IsActive && !x.IsDeleted, ct)
            ?? throw new InvalidOperationException("No active trading account found for the FXCM broker");

        return new FxcmCredentials(account.AccountId, broker.ApiKey, broker.BaseUrl);
    }

    /// <summary>
    /// Converts engine symbol format (e.g. "EURUSD") to FXCM format (e.g. "EUR/USD").
    /// </summary>
    internal static string ToFxcmSymbol(string symbol)
    {
        if (symbol.Length == 6)
            return $"{symbol[..3]}/{symbol[3..]}";
        return symbol; // pass through non-standard symbols
    }

    private record FxcmCredentials(string AccountId, string? ApiKey, string BaseUrl);
}

/// <summary>
/// FXCM data feed scaffold.
/// </summary>
[RegisterKeyedService(typeof(IBrokerDataFeed), BrokerType.Fxcm, ServiceLifetime.Singleton)]
public sealed class FxcmBrokerAdapter : IBrokerDataFeed
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FxcmBrokerAdapter> _logger;

    public FxcmBrokerAdapter(IServiceScopeFactory scopeFactory, ILogger<FxcmBrokerAdapter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public Task SubscribeAsync(
        IEnumerable<string> symbols, Func<Tick, Task> onTick, CancellationToken cancellationToken)
    {
        // TODO: FXCM uses Socket.IO for streaming prices
        // 1. POST /subscribe  { "pairs": "EUR/USD,GBP/USD" }
        // 2. Listen on socket event "Model.Price" for tick updates
        // 3. Map FXCM tick → Tick record and invoke onTick

        _logger.LogWarning("FxcmBrokerAdapter.SubscribeAsync: not yet implemented");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BrokerCandle>> GetCandlesAsync(
        string symbol, string timeframe, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        // TODO: GET /candles/{offer_id}/{period_id}?num={count}&from={epoch}&to={epoch}
        // Period mapping: M1, M5, M15, M30, H1, H4, D1, W1
        // Must first resolve offer_id via GET /trading/get_model?models=Offer

        _logger.LogWarning("FxcmBrokerAdapter.GetCandlesAsync: not yet implemented");
        return Task.FromResult<IReadOnlyList<BrokerCandle>>(Array.Empty<BrokerCandle>());
    }
}
