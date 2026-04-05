using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services.NlpSentiment;

/// <summary>
/// Parses RSS 2.0 and Atom feeds into <see cref="HeadlineItem"/> records.
/// Uses <see cref="XDocument"/> so no additional NuGet packages are required.
/// </summary>
[RegisterService(ServiceLifetime.Scoped)]
public sealed class RssFeedParser
{
    private readonly ILogger<RssFeedParser> _logger;

    public RssFeedParser(ILogger<RssFeedParser> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<HeadlineItem>> FetchAndParseAsync(
        string feedUrl, HttpClient httpClient, int maxAgeDays, CancellationToken ct)
    {
        var items = new List<HeadlineItem>();
        try
        {
            var response = await httpClient.GetStringAsync(feedUrl, ct);
            var doc = XDocument.Parse(response);
            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);

            // RSS 2.0: /rss/channel/item
            foreach (var item in doc.Descendants("item"))
            {
                var title = item.Element("title")?.Value?.Trim();
                var desc = item.Element("description")?.Value?.Trim();
                var pubDate = ParseDate(item.Element("pubDate")?.Value);
                if (title != null && pubDate >= cutoff)
                    items.Add(new HeadlineItem(title, desc, pubDate, feedUrl));
            }

            // Atom: /feed/entry
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            foreach (var entry in doc.Descendants(ns + "entry"))
            {
                var title = entry.Element(ns + "title")?.Value?.Trim();
                var summary = entry.Element(ns + "summary")?.Value?.Trim();
                var updated = ParseDate(entry.Element(ns + "updated")?.Value);
                if (title != null && updated >= cutoff)
                    items.Add(new HeadlineItem(title, summary, updated, feedUrl));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RssFeedParser: failed to fetch/parse {Url}", feedUrl);
        }

        return items;
    }

    private static DateTime ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DateTime.MinValue;
        return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt) ? dt : DateTime.MinValue;
    }
}
