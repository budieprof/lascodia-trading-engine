namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Synchronizes the latest published CFTC Commitment of Traders reports needed by the engine.
/// </summary>
public interface ICOTReportSyncService
{
    /// <summary>
    /// Fetches and persists the latest published COT reports for currencies referenced by
    /// active instruments, repairing any stale stored rows when the published payload changed.
    /// </summary>
    Task<COTReportSyncResult> SyncLatestPublishedReportsAsync(CancellationToken ct);
}

/// <summary>
/// Summary of a single COT synchronization pass.
/// </summary>
public sealed record COTReportSyncResult(
    int ActivePairCount,
    int CurrencyCount,
    int SupportedCurrencyCount,
    int UnsupportedCurrencyCount,
    int PublishedReportCount,
    int CreatedCount,
    int RepairedCount,
    int UnchangedCount,
    int UnavailableCount,
    int FetchFailedCount,
    int PersistFailedCount,
    string? SkippedReason = null)
{
    public int FailedCount => FetchFailedCount + PersistFailedCount;

    public int PendingCount => UnavailableCount + FailedCount;

    public static COTReportSyncResult Skipped(string reason) =>
        new(
            ActivePairCount: 0,
            CurrencyCount: 0,
            SupportedCurrencyCount: 0,
            UnsupportedCurrencyCount: 0,
            PublishedReportCount: 0,
            CreatedCount: 0,
            RepairedCount: 0,
            UnchangedCount: 0,
            UnavailableCount: 0,
            FetchFailedCount: 0,
            PersistFailedCount: 0,
            SkippedReason: reason);
}
