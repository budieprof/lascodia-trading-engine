namespace LascodiaTradingEngine.Application.Common.Interfaces;

public sealed record HeadlineItem(string Headline, string? Summary, DateTime PublishedAt, string SourceFeed);

public sealed record EconomicEventItem(
    string Title, string Currency, string Impact,
    string? Forecast, string? Previous, string? Actual, DateTime ScheduledAt);

public sealed record CurrencySentimentResult(
    string Currency, decimal SentimentScore, decimal Confidence,
    decimal BullishPct, decimal BearishPct, decimal NeutralPct, string Rationale);

public interface IDeepSeekSentimentService
{
    Task<IReadOnlyList<CurrencySentimentResult>> AnalyzeHeadlinesAsync(
        IReadOnlyList<HeadlineItem> headlines, CancellationToken ct);

    Task<IReadOnlyList<CurrencySentimentResult>> AnalyzeEconomicEventsAsync(
        IReadOnlyList<EconomicEventItem> events, CancellationToken ct);
}
