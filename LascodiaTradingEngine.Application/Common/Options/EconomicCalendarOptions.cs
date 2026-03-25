using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Configurable settings for <see cref="Workers.EconomicCalendarWorker"/>.
/// Bound from the <c>EconomicCalendarOptions</c> section in appsettings.json.
/// </summary>
public class EconomicCalendarOptions : ConfigurationOption<EconomicCalendarOptions>
{
    /// <summary>
    /// How often (in hours) the worker wakes up to sync calendar data with the external feed.
    /// Defaults to 6 hours. Reduce on high-impact release days for faster actuals patching.
    /// </summary>
    public double PollingIntervalHours { get; set; } = 6;

    /// <summary>
    /// How far ahead (in days) to fetch upcoming events on each ingestion pass.
    /// Defaults to 7 days, covering the full trading week ahead.
    /// </summary>
    public int LookaheadDays { get; set; } = 7;

    /// <summary>
    /// Maximum number of past events to patch actuals for per polling cycle.
    /// Limits the number of individual API calls to the calendar feed per pass.
    /// Defaults to 50.
    /// </summary>
    public int ActualsPatchBatchSize { get; set; } = 50;

    /// <summary>
    /// Maximum age (in days) of a past event that the actuals patch pass will still attempt
    /// to resolve. Events older than this with no actual value are considered stale and skipped.
    /// Prevents indefinite retries on cancelled or permanently unavailable releases.
    /// Defaults to 7 days.
    /// </summary>
    public int StaleEventCutoffDays { get; set; } = 7;

    /// <summary>
    /// Timeout (in seconds) for individual feed API calls (GetUpcomingEventsAsync, GetActualAsync).
    /// Prevents a hung HTTP call from blocking the entire polling cycle.
    /// Defaults to 30 seconds.
    /// </summary>
    public int FeedCallTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of retries for transient feed errors during the ingestion pass.
    /// Uses exponential backoff (200ms × 2^attempt). Defaults to 2 retries (3 total attempts).
    /// </summary>
    public int FeedRetryCount { get; set; } = 2;

    /// <summary>
    /// Number of retries for transient feed errors when patching a single event's actual value.
    /// Uses the same exponential backoff as the ingestion pass. Defaults to 1 retry (2 total attempts).
    /// </summary>
    public int ActualsPatchRetryCount { get; set; } = 1;

    /// <summary>
    /// Maximum number of concurrent feed API calls during the actuals patch pass.
    /// Prevents overwhelming the calendar feed while still processing faster than sequential.
    /// Defaults to 5.
    /// </summary>
    public int ActualsPatchMaxConcurrency { get; set; } = 5;

    /// <summary>
    /// When true, the worker skips polling cycles on Saturdays and Sundays (UTC) since
    /// economic releases are almost never scheduled on weekends. Defaults to true.
    /// </summary>
    public bool SkipWeekends { get; set; } = true;

    /// <summary>
    /// Number of consecutive ingestion fetch failures (all retries exhausted) before the
    /// worker stops attempting ingestion and enters a cooldown. The cooldown lasts for
    /// this many polling cycles, after which the worker retries once and resets if successful.
    /// Reduces noise and feed pressure when the upstream calendar is hard-down.
    /// Defaults to 3.
    /// </summary>
    public int FeedCircuitBreakerThreshold { get; set; } = 3;

    /// <summary>
    /// Number of consecutive polling cycles returning zero events before a critical alert
    /// is raised. Indicates a possible feed structural change or blocking.
    /// Defaults to 3.
    /// </summary>
    public int SustainedEmptyFetchThreshold { get; set; } = 3;
}
