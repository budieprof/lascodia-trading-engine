using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.MLModels.Shared;

/// <summary>
/// Point-in-time COT normalization snapshot used by feature-vector workers.
/// Mirrors ML training semantics by choosing the latest report available at a
/// bar timestamp and normalizing against the full historical range for the base
/// currency.
/// </summary>
internal sealed class CotFeatureLookupSnapshot
{
    private static readonly CotFeatureLookupSnapshot Empty = new(
        Array.Empty<COTReport>(),
        netMin: -300_000f,
        netMax: 300_000f,
        momMin: -30_000f,
        momMax: 30_000f);

    private readonly IReadOnlyList<COTReport> _reports;
    private readonly float _netMin;
    private readonly float _netMax;
    private readonly float _momMin;
    private readonly float _momMax;

    private CotFeatureLookupSnapshot(
        IReadOnlyList<COTReport> reports,
        float netMin,
        float netMax,
        float momMin,
        float momMax)
    {
        _reports = reports;
        _netMin = netMin;
        _netMax = netMax;
        _momMin = momMin;
        _momMax = momMax;
    }

    public static async Task<CotFeatureLookupSnapshot> LoadAsync(
        DbContext db,
        string symbol,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Length < 3)
            return Empty;

        string baseCurrency = symbol[..3];

        var reports = await db.Set<COTReport>()
            .AsNoTracking()
            .Where(report => !report.IsDeleted && report.Currency == baseCurrency)
            .OrderBy(report => report.ReportDate)
            .ToListAsync(ct);

        if (reports.Count == 0)
            return Empty;

        float netMin = (float)reports.Min(report => (double)report.NetNonCommercialPositioning);
        float netMax = (float)reports.Max(report => (double)report.NetNonCommercialPositioning);
        float momMin = (float)reports.Min(report => (double)report.NetPositioningChangeWeekly);
        float momMax = (float)reports.Max(report => (double)report.NetPositioningChangeWeekly);

        if (netMax - netMin < 1f)
        {
            netMin = -300_000f;
            netMax = 300_000f;
        }

        if (momMax - momMin < 1f)
        {
            momMin = -30_000f;
            momMax = 30_000f;
        }

        return new CotFeatureLookupSnapshot(reports, netMin, netMax, momMin, momMax);
    }

    public CotFeatureEntry Resolve(DateTime asOfUtc)
    {
        if (_reports.Count == 0)
            return CotFeatureEntry.Zero;

        var report = _reports.LastOrDefault(candidate => candidate.ReportDate <= asOfUtc);
        if (report is null)
            return CotFeatureEntry.Zero;

        float netRange = _netMax - _netMin;
        float momRange = _momMax - _momMin;
        float netNorm = MLFeatureHelper.Clamp(
            ((float)(double)report.NetNonCommercialPositioning - _netMin) / netRange * 6f - 3f,
            -3f,
            3f);
        float momentum = MLFeatureHelper.Clamp(
            ((float)(double)report.NetPositioningChangeWeekly - _momMin) / momRange * 6f - 3f,
            -3f,
            3f);

        return new CotFeatureEntry(netNorm, momentum);
    }
}
