using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the news sentiment polling worker.</summary>
public class NewsSentimentOptions : ConfigurationOption<NewsSentimentOptions>
{
    public bool Enabled { get; set; }
    public double PollingIntervalHours { get; set; } = 2.0;
    /// <summary>
    /// RSS/Atom feed URLs to poll for financial news. Must be configured per deployment.
    /// Examples: ForexFactory, FXStreet, DailyFX, Reuters, Bloomberg RSS endpoints.
    /// Left empty by default — configure in appsettings or EngineConfig.
    /// </summary>
    public List<string> RssFeedUrls { get; set; } = new();
    public int MaxHeadlineAgeDays { get; set; } = 1;
    public int EconomicEventLookbackHours { get; set; } = 24;
    public int EconomicEventLookaheadHours { get; set; } = 6;
    public bool SkipWeekends { get; set; } = true;
    public int MaxCallsPerCycle { get; set; } = 10;
    public int DeduplicationWindowHours { get; set; } = 4;
}
