using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// <see cref="ISentimentFeed"/> implementation that delegates to the
/// <see cref="IDeepSeekSentimentService"/> NLP pipeline. Fetches recent economic
/// events from the database and sends them through DeepSeek for sentiment analysis.
/// Registered as Scoped — resolves within the same DI scope as the calling worker.
/// </summary>
public sealed class DeepSeekSentimentFeed : ISentimentFeed
{
    private readonly IReadApplicationDbContext _readCtx;
    private readonly IDeepSeekSentimentService _deepSeek;
    private readonly ILogger<DeepSeekSentimentFeed> _logger;

    public string SourceName => "DeepSeekNLP";

    public DeepSeekSentimentFeed(
        IReadApplicationDbContext readCtx,
        IDeepSeekSentimentService deepSeek,
        ILogger<DeepSeekSentimentFeed> logger)
    {
        _readCtx  = readCtx;
        _deepSeek = deepSeek;
        _logger   = logger;
    }

    public async Task<SentimentReading?> FetchAsync(string symbol, CancellationToken ct)
    {
        if (symbol.Length < 6) return null;

        string baseCurrency  = symbol[..3];
        string quoteCurrency = symbol[3..6];

        var readDb = _readCtx.GetDbContext();

        // Load recent economic events for both currencies (last 24 hours)
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var recentEvents = await readDb.Set<EconomicEvent>()
            .Where(e => !e.IsDeleted
                     && e.ScheduledAt >= cutoff
                     && (e.Currency == baseCurrency || e.Currency == quoteCurrency))
            .OrderByDescending(e => e.ScheduledAt)
            .Take(20)
            .ToListAsync(ct);

        if (recentEvents.Count == 0)
            return null;

        var eventItems = recentEvents.Select(e => new EconomicEventItem(
            e.Title, e.Currency, e.Impact.ToString(),
            e.Forecast, e.Previous, e.Actual, e.ScheduledAt)).ToList();

        try
        {
            var results = await _deepSeek.AnalyzeEconomicEventsAsync(eventItems, ct);

            // Find the result for the base currency (primary driver)
            var baseResult = results.FirstOrDefault(r =>
                r.Currency.Equals(baseCurrency, StringComparison.OrdinalIgnoreCase));

            if (baseResult is null) return null;

            return new SentimentReading(
                baseResult.SentimentScore,
                baseResult.BullishPct,
                baseResult.BearishPct,
                baseResult.NeutralPct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeepSeekSentimentFeed: NLP analysis failed for {Symbol}, returning null", symbol);
            return null;
        }
    }
}
