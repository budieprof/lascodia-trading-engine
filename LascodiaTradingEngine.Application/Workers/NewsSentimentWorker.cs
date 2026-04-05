using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediatR;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services.NlpSentiment;
using LascodiaTradingEngine.Application.Sentiment.Commands.RecordSentiment;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Processes financial news headlines (from RSS feeds) and economic event records
/// through DeepSeek V3 NLP to produce currency-level sentiment snapshots.
/// Runs every 2 hours (configurable). Separate from SentimentWorker to allow
/// independent scheduling and enable/disable.
/// </summary>
public class NewsSentimentWorker : BackgroundService
{
    private readonly ILogger<NewsSentimentWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NewsSentimentOptions _options;
    private readonly DeepSeekOptions _deepSeekOptions;
    private readonly TradingMetrics _metrics;

    public NewsSentimentWorker(
        ILogger<NewsSentimentWorker> logger,
        IServiceScopeFactory scopeFactory,
        NewsSentimentOptions options,
        DeepSeekOptions deepSeekOptions,
        TradingMetrics metrics)
    {
        _logger          = logger;
        _scopeFactory    = scopeFactory;
        _options         = options;
        _deepSeekOptions = deepSeekOptions;
        _metrics         = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NewsSentimentWorker starting");

        if (!_options.Enabled || !_deepSeekOptions.IsConfigured)
        {
            _logger.LogInformation(
                "NewsSentimentWorker disabled (Enabled={Enabled}, ApiKeyConfigured={ApiKey})",
                _options.Enabled, _deepSeekOptions.IsConfigured);
            return;
        }

        // Let other workers initialise first (market data, calendar, etc.)
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_options.Enabled || !_deepSeekOptions.IsConfigured)
                {
                    _logger.LogDebug("NewsSentimentWorker: disabled mid-run, skipping cycle");
                    await Task.Delay(TimeSpan.FromHours(_options.PollingIntervalHours), stoppingToken);
                    continue;
                }

                // Weekend skip
                if (_options.SkipWeekends)
                {
                    var dayOfWeek = DateTime.UtcNow.DayOfWeek;
                    if (dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    {
                        _logger.LogDebug("NewsSentimentWorker: skipping weekend cycle");
                        await Task.Delay(TimeSpan.FromHours(_options.PollingIntervalHours), stoppingToken);
                        continue;
                    }
                }

                var cycleStart = System.Diagnostics.Stopwatch.GetTimestamp();

                using var scope = _scopeFactory.CreateScope();
                var deepSeekService = scope.ServiceProvider.GetRequiredService<IDeepSeekSentimentService>();
                var readContext     = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var mediator        = scope.ServiceProvider.GetRequiredService<IMediator>();
                var rssParser       = scope.ServiceProvider.GetRequiredService<RssFeedParser>();
                var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                var rssClient       = httpClientFactory.CreateClient("RssFeed");

                int cycleCallCount = 0;

                // ── Phase A: RSS Feed Processing ────────────────────────────────
                var allHeadlines = new List<HeadlineItem>();
                var seenTitles   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var feedUrl in _options.RssFeedUrls)
                {
                    try
                    {
                        var headlines = await rssParser.FetchAndParseAsync(
                            feedUrl, rssClient, _options.MaxHeadlineAgeDays, stoppingToken);

                        foreach (var h in headlines)
                        {
                            // Deduplicate by headline title across feeds
                            if (seenTitles.Add(h.Headline))
                                allHeadlines.Add(h);
                        }

                        _logger.LogDebug(
                            "NewsSentimentWorker: fetched {Count} headlines from {Url}",
                            headlines.Count, feedUrl);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "NewsSentimentWorker: failed to fetch RSS feed {Url}", feedUrl);
                    }
                }

                if (allHeadlines.Count > 0)
                {
                    if (cycleCallCount >= _options.MaxCallsPerCycle)
                    {
                        _logger.LogInformation("NewsSentimentWorker: reached MaxCallsPerCycle ({Max}) before headline analysis, deferring to next cycle", _options.MaxCallsPerCycle);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "NewsSentimentWorker: analyzing {Count} deduplicated headlines via DeepSeek",
                            allHeadlines.Count);

                        cycleCallCount++;
                        _metrics.NewsSentimentCallsTotal.Add(1);

                        try
                        {
                            var headlineResults = await deepSeekService.AnalyzeHeadlinesAsync(allHeadlines, stoppingToken);

                            _metrics.NewsSentimentHeadlinesProcessed.Add(allHeadlines.Count);

                            foreach (var result in headlineResults)
                            {
                                await mediator.Send(new RecordSentimentCommand
                                {
                                    Symbol         = result.Currency,
                                    Source         = "NewsSentiment",
                                    SentimentScore = result.SentimentScore,
                                    BullishPct     = result.BullishPct,
                                    BearishPct     = result.BearishPct,
                                    NeutralPct     = result.NeutralPct,
                                    Confidence     = result.Confidence
                                }, stoppingToken);

                                _logger.LogDebug(
                                    "NewsSentimentWorker: recorded headline sentiment for {Currency} — Score={Score:F2}, Confidence={Confidence:F2}",
                                    result.Currency, result.SentimentScore, result.Confidence);
                            }
                        }
                        catch (Exception ex)
                        {
                            _metrics.NewsSentimentCallErrors.Add(1);
                            _logger.LogError(ex, "NewsSentimentWorker: DeepSeek headline analysis failed");
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("NewsSentimentWorker: no headlines to analyze");
                }

                // ── Phase B: Economic Event Processing ──────────────────────────
                var economicEventsCount = 0;

                if (cycleCallCount >= _options.MaxCallsPerCycle)
                {
                    _logger.LogInformation("NewsSentimentWorker: reached MaxCallsPerCycle ({Max}), deferring remaining to next cycle", _options.MaxCallsPerCycle);
                }
                else
                {
                    var now       = DateTime.UtcNow;
                    var lookback  = now.AddHours(-_options.EconomicEventLookbackHours);
                    var lookahead = now.AddHours(_options.EconomicEventLookaheadHours);

                    var economicEvents = await readContext.GetDbContext()
                        .Set<EconomicEvent>()
                        .Where(e => !e.IsDeleted
                            && e.Impact >= EconomicImpact.Medium
                            && e.ScheduledAt >= lookback
                            && e.ScheduledAt <= lookahead)
                        .OrderByDescending(e => e.Impact)
                        .ThenBy(e => e.ScheduledAt)
                        .ToListAsync(stoppingToken);

                    economicEventsCount = economicEvents.Count;

                    if (economicEvents.Count > 0)
                    {
                        _logger.LogInformation(
                            "NewsSentimentWorker: analyzing {Count} economic events via DeepSeek",
                            economicEvents.Count);

                        var eventItems = economicEvents.Select(e => new EconomicEventItem(
                            e.Title,
                            e.Currency,
                            e.Impact.ToString(),
                            e.Forecast,
                            e.Previous,
                            e.Actual,
                            e.ScheduledAt
                        )).ToList();

                        cycleCallCount++;
                        _metrics.NewsSentimentCallsTotal.Add(1);

                        try
                        {
                            var eventResults = await deepSeekService.AnalyzeEconomicEventsAsync(eventItems, stoppingToken);

                            _metrics.NewsSentimentEventsProcessed.Add(economicEvents.Count);

                            foreach (var result in eventResults)
                            {
                                await mediator.Send(new RecordSentimentCommand
                                {
                                    Symbol         = result.Currency,
                                    Source         = "NewsSentiment",
                                    SentimentScore = result.SentimentScore,
                                    BullishPct     = result.BullishPct,
                                    BearishPct     = result.BearishPct,
                                    NeutralPct     = result.NeutralPct,
                                    Confidence     = result.Confidence
                                }, stoppingToken);

                                _logger.LogDebug(
                                    "NewsSentimentWorker: recorded event sentiment for {Currency} — Score={Score:F2}, Confidence={Confidence:F2}",
                                    result.Currency, result.SentimentScore, result.Confidence);
                            }
                        }
                        catch (Exception ex)
                        {
                            _metrics.NewsSentimentCallErrors.Add(1);
                            _logger.LogError(ex, "NewsSentimentWorker: DeepSeek economic event analysis failed");
                        }
                    }
                    else
                    {
                        _logger.LogDebug("NewsSentimentWorker: no economic events in window");
                    }
                }

                // Record cycle duration
                var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
                _metrics.NewsSentimentCycleDurationMs.Record(elapsedMs);

                _logger.LogInformation(
                    "NewsSentimentWorker: cycle complete in {ElapsedMs:F0}ms — {Headlines} headlines, {Events} events",
                    elapsedMs, allHeadlines.Count, economicEventsCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in NewsSentimentWorker polling loop");
            }

            await Task.Delay(TimeSpan.FromHours(_options.PollingIntervalHours), stoppingToken);
        }

        _logger.LogInformation("NewsSentimentWorker stopped");
    }
}
