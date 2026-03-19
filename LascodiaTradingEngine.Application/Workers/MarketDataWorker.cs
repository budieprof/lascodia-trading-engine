using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketData.Commands.IngestCandle;
using LascodiaTradingEngine.Application.MarketData.Commands.UpdateLiveCandle;
using LascodiaTradingEngine.Application.Services.BrokerAdapters;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that connects to the broker data feed, streams live ticks into the
/// in-memory price cache, and persists closed candles to the database.
/// </summary>
public class MarketDataWorker : BackgroundService
{
    private readonly ILogger<MarketDataWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBrokerDataFeed _feed;

    public MarketDataWorker(
        ILogger<MarketDataWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBrokerDataFeed feed)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _feed         = feed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MarketDataWorker starting");

        var symbols = await GetActiveSymbolsAsync(stoppingToken);

        if (symbols.Count == 0)
        {
            _logger.LogWarning("No active currency pairs found — MarketDataWorker idle");
            return;
        }

        _logger.LogInformation("Subscribing to {Count} symbols: {Symbols}",
            symbols.Count, string.Join(", ", symbols));

        await _feed.SubscribeAsync(symbols, OnTickAsync, stoppingToken);

        _logger.LogInformation("MarketDataWorker stopped");
    }

    private async Task OnTickAsync(Tick tick)
    {
        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Update in-memory cache + publish integration event
        await mediator.Send(new UpdateLiveCandleCommand
        {
            Symbol    = tick.Symbol,
            Bid       = tick.Bid,
            Ask       = tick.Ask,
            Timestamp = tick.Timestamp
        });
    }

    private async Task<List<string>> GetActiveSymbolsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        return await context.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .Select(x => x.Symbol)
            .ToListAsync(cancellationToken);
    }
}
