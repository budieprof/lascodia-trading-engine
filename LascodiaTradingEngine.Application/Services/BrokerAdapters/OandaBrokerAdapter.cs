using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

/// <summary>
/// Stub adapter for the OANDA v20 REST/Streaming API.
/// Replace the method bodies with real HTTP calls using the OANDA SDK or HttpClient.
/// Credentials are loaded from the active Broker/TradingAccount DB records.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class OandaBrokerAdapter : IBrokerDataFeed
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OandaBrokerAdapter> _logger;

    public OandaBrokerAdapter(IServiceScopeFactory scopeFactory, ILogger<OandaBrokerAdapter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task SubscribeAsync(
        IEnumerable<string> symbols,
        Func<Tick, Task> onTick,
        CancellationToken cancellationToken)
    {
        var (accountId, apiKey, baseUrl) = await GetCredentialsAsync(cancellationToken);

        // TODO: implement OANDA streaming pricing endpoint
        // GET /v3/accounts/{accountId}/pricing/stream?instruments={csv}
        _logger.LogWarning(
            "OandaBrokerAdapter.SubscribeAsync is not yet implemented — account={AccountId} baseUrl={BaseUrl}",
            accountId, baseUrl);

        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<BrokerCandle>> GetCandlesAsync(
        string symbol, string timeframe, DateTime from, DateTime to,
        CancellationToken cancellationToken)
    {
        var (accountId, apiKey, baseUrl) = await GetCredentialsAsync(cancellationToken);

        // TODO: implement OANDA instrument candles endpoint
        // GET /v3/instruments/{instrument}/candles
        _logger.LogWarning(
            "OandaBrokerAdapter.GetCandlesAsync is not yet implemented — account={AccountId} baseUrl={BaseUrl}",
            accountId, baseUrl);

        return Array.Empty<BrokerCandle>();
    }

    private async Task<(string accountId, string? apiKey, string baseUrl)> GetCredentialsAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db            = readContext.GetDbContext();

        var broker = await db.Set<Domain.Entities.Broker>()
            .FirstOrDefaultAsync(x => x.BrokerType == BrokerType.Oanda && x.IsActive && !x.IsDeleted, ct);

        if (broker == null)
            throw new InvalidOperationException("No active Oanda broker configured in database");

        var account = await db.Set<Domain.Entities.TradingAccount>()
            .FirstOrDefaultAsync(x => x.BrokerId == broker.Id && x.IsActive && !x.IsDeleted, ct);

        if (account == null)
            throw new InvalidOperationException("No active trading account found for the active Oanda broker");

        return (account.AccountId, broker.ApiKey, broker.BaseUrl);
    }
}
