using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>RecordSentimentCommandHandler</c> after a sentiment row
/// lands. The admin UI's sentiment page subscribes to this via the realtime
/// relay so the card refreshes without polling — PRD §E1 push-vs-poll.
/// </summary>
public record SentimentSnapshotCreatedIntegrationEvent : IntegrationEvent
{
    public long     SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>DB id of the newly-created snapshot.</summary>
    public long     SnapshotId     { get; init; }

    /// <summary>Currency / symbol the snapshot describes.</summary>
    public string   Symbol         { get; init; } = string.Empty;

    /// <summary>Canonical source: COT / NewsSentiment / AutoFeed.</summary>
    public string   Source         { get; init; } = string.Empty;

    /// <summary>Normalised sentiment score in [-1, 1].</summary>
    public decimal  SentimentScore { get; init; }

    /// <summary>UTC timestamp of the captured snapshot.</summary>
    public DateTime CapturedAt     { get; init; }
}
