using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Threading;
using LascodiaTradingEngine.Application.Common.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.COTData;

/// <summary>
/// Fetches the latest published COT positioning data from the CFTC's public bulk CSV files.
/// </summary>
/// <remarks>
/// The CFTC publishes the Legacy COT report as a yearly ZIP archive containing a single
/// <c>annual.txt</c> CSV file at:
/// <c>https://www.cftc.gov/files/dea/history/deacot{year}.zip</c>.
/// The archive contains currency futures rows (e.g. "EURO FX - CHICAGO MERCANTILE EXCHANGE")
/// with the Legacy column layout this class parses (<c>Open Interest (All)</c>,
/// <c>Noncommercial Positions-Long (All)</c>, etc).
///
/// Each row in the CSV represents one commodity/date combination. Currency futures rows are
/// identified by the "FOREX" market code in the <c>Market_and_Exchange_Names</c> column or
/// by matching known CFTC contract codes for currency futures.
///
/// <b>Known CFTC currency contract mappings:</b>
/// EUR → "EURO FX", GBP → "BRITISH POUND", JPY → "JAPANESE YEN",
/// AUD → "AUSTRALIAN DOLLAR", CAD → "CANADIAN DOLLAR", CHF → "SWISS FRANC",
/// NZD → "NZ DOLLAR", MXN → "MEXICAN PESO", ZAR → "SOUTH AFRICAN RAND",
/// BRL → "BRAZILIAN REAL", RUB → "RUSSIAN RUBLE", USD → "USD INDEX".
///
/// <b>Caching:</b> The entire year's CSV is cached in memory for 1 hour after download,
/// which keeps per-cycle lookups cheap while still allowing the hourly worker to observe
/// a newly published Friday release promptly.
/// </remarks>
public class CftcCOTDataFeed : ICOTDataFeed
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CftcCOTDataFeed> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<int, Lazy<Task<List<CftcCsvRecord>?>>> _inflightYearLoads = new();

    /// <summary>How long to cache a downloaded year's parsed COT data.</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// Maps ISO 4217 currency codes to CFTC contract name substrings as they appear in the
    /// <c>Market_and_Exchange_Names</c> CSV column. The CFTC uses full English names.
    /// </summary>
    private static readonly Dictionary<string, string> CurrencyToContractName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EUR"] = "EURO FX",
        ["GBP"] = "BRITISH POUND",
        ["JPY"] = "JAPANESE YEN",
        ["AUD"] = "AUSTRALIAN DOLLAR",
        ["CAD"] = "CANADIAN DOLLAR",
        ["CHF"] = "SWISS FRANC",
        ["NZD"] = "NZ DOLLAR",
        ["MXN"] = "MEXICAN PESO",
        ["ZAR"] = "SOUTH AFRICAN RAND",
        ["BRL"] = "BRAZILIAN REAL",
        ["RUB"] = "RUSSIAN RUBLE",
        ["USD"] = "USD INDEX",
        ["SEK"] = "SWEDISH KRONA",
        ["NOK"] = "NORWEGIAN KRONE",
        ["SGD"] = "SINGAPORE DOLLAR",
        ["KRW"] = "SOUTH KOREAN WON",
        ["INR"] = "INDIAN RUPEE",
        ["PLN"] = "POLISH ZLOTY",
        ["CZK"] = "CZECH KORUNA",
        ["ILS"] = "ISRAELI SHEKEL",
        ["HUF"] = "HUNGARIAN FORINT",
        ["TRY"] = "TURKISH LIRA",
        ["CNH"] = "CHINESE RENMINBI",
        ["CNY"] = "CHINESE RENMINBI",
    };

    public CftcCOTDataFeed(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<CftcCOTDataFeed> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _cache             = cache;
        _logger            = logger;
        _timeProvider      = timeProvider ?? TimeProvider.System;
    }

    public bool SupportsCurrency(string currency)
    {
        currency = currency.ToUpperInvariant();
        return CurrencyToContractName.ContainsKey(currency);
    }

    /// <inheritdoc/>
    public async Task<COTPositioningData?> GetLatestPublishedReportAsync(string currency, CancellationToken ct)
    {
        currency = currency.ToUpperInvariant();

        if (!CurrencyToContractName.TryGetValue(currency, out var contractName))
            return null;

        var yearTasks = GetCandidateYears()
            .Distinct()
            .Select(year => GetYearRecordsAsync(year, ct))
            .ToList();

        var candidateRecords = (await Task.WhenAll(yearTasks))
            .Where(records => records != null)
            .SelectMany(records => records!)
            .Where(r => r.ContractName.Contains(contractName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var match = candidateRecords
            .OrderByDescending(r => r.ReportDate)
            .FirstOrDefault();

        if (match == null)
        {
            _logger.LogDebug(
                "CftcCOTDataFeed: no published COT record found for {Currency} ({Contract})",
                currency, contractName);
            return null;
        }

        return new COTPositioningData(
            ReportDate:        match.ReportDate.Date,
            CommercialLong:     match.CommercialLong,
            CommercialShort:    match.CommercialShort,
            NonCommercialLong:  match.NonCommercialLong,
            NonCommercialShort: match.NonCommercialShort,
            RetailLong:         match.NonReportableLong,
            RetailShort:        match.NonReportableShort,
            TotalOpenInterest:  match.OpenInterest);
    }

    private IEnumerable<int> GetCandidateYears()
    {
        int currentYear = _timeProvider.GetUtcNow().UtcDateTime.Year;
        yield return currentYear;
        yield return currentYear - 1;
    }

    /// <summary>
    /// Downloads and parses the CFTC bulk CSV for the given year, with in-memory caching.
    /// </summary>
    private async Task<List<CftcCsvRecord>?> GetYearRecordsAsync(int year, CancellationToken ct)
    {
        string cacheKey = BuildCacheKey(year);

        if (_cache.TryGetValue(cacheKey, out List<CftcCsvRecord>? cached))
            return cached;

        var lazyLoad = _inflightYearLoads.GetOrAdd(
            year,
            static (requestedYear, self) => new Lazy<Task<List<CftcCsvRecord>?>>(
                () => self.LoadYearRecordsAsync(requestedYear),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this);

        try
        {
            return await lazyLoad.Value.WaitAsync(ct);
        }
        finally
        {
            if (lazyLoad.IsValueCreated && lazyLoad.Value.IsCompleted)
            {
                _inflightYearLoads.TryRemove(
                    new KeyValuePair<int, Lazy<Task<List<CftcCsvRecord>?>>>(year, lazyLoad));
            }
        }
    }

    private async Task<List<CftcCsvRecord>?> LoadYearRecordsAsync(int year)
    {
        string cacheKey = BuildCacheKey(year);

        if (_cache.TryGetValue(cacheKey, out List<CftcCsvRecord>? cached))
            return cached;

        var records = await DownloadAndParseAsync(year, CancellationToken.None);
        if (records == null)
            return null;

        _cache.Set(cacheKey, records, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            Size = 1
        });

        return records;
    }

    private static string BuildCacheKey(int year) => $"cftc_cot_{year}";

    /// <summary>
    /// Downloads the CFTC Legacy COT ZIP for the given year and parses its CSV contents.
    /// </summary>
    private async Task<List<CftcCsvRecord>?> DownloadAndParseAsync(int year, CancellationToken ct)
    {
        var urls = new[]
        {
            $"https://www.cftc.gov/files/dea/history/deacot{year}.zip"
        };

        var client = _httpClientFactory.CreateClient("CftcCOT");

        foreach (var url in urls)
        {
            try
            {
                _logger.LogInformation("CftcCOTDataFeed: downloading COT data from {Url}", url);

                using var response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("CftcCOTDataFeed: {Url} returned {Status}", url, response.StatusCode);
                    continue;
                }

                // Buffer into MemoryStream — ZipArchive in Read mode needs a seekable
                // stream to locate the central directory at the end of the archive.
                using var memoryStream = new MemoryStream();
                await (await response.Content.ReadAsStreamAsync(ct)).CopyToAsync(memoryStream, ct);
                memoryStream.Position = 0;

                var records = ParseZipCsv(memoryStream);

                _logger.LogInformation(
                    "CftcCOTDataFeed: parsed {Count} records from {Url}",
                    records.Count, url);

                return records;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "CftcCOTDataFeed: failed to download {Url}", url);
            }
        }

        // Downgraded from Error to Debug — this commonly occurs for the current year
        // before CFTC has published the first ZIP (release lag is typically 1–2 weeks
        // into January) and the caller in GetLatestPublishedReportAsync now falls back
        // to year-1.
        // An unhandled download failure is still surfaced via the LogWarning at each URL
        // attempt, so operators retain visibility without one Error-level line per symbol.
        _logger.LogDebug("CftcCOTDataFeed: no COT archive available for year {Year} — caller will fall back", year);
        return null;
    }

    /// <summary>
    /// Extracts the first CSV file from a CFTC ZIP archive and parses it into records.
    /// </summary>
    private List<CftcCsvRecord> ParseZipCsv(Stream zipStream)
    {
        var records = new List<CftcCsvRecord>();

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var csvEntry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

        if (csvEntry == null)
        {
            _logger.LogWarning("CftcCOTDataFeed: no CSV/TXT file found in ZIP archive");
            return records;
        }

        using var entryStream = csvEntry.Open();
        using var reader = new StreamReader(entryStream);

        // Read header line and build column index map
        var headerLine = reader.ReadLine();
        if (headerLine == null)
            return records;

        var headers = ParseCsvLine(headerLine);
        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
            columnIndex[headers[i].Trim()] = i;

        // CFTC legacy CSV column names (case-insensitive matching)
        if (!TryGetColumnIndices(columnIndex, out var indices))
        {
            _logger.LogError("CftcCOTDataFeed: CSV header missing required columns. Headers: {Headers}",
                string.Join(", ", headers.Take(20)));
            return records;
        }

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseCsvLine(line);
            if (fields.Length <= indices.MaxIndex)
                continue;

            try
            {
                var record = new CftcCsvRecord
                {
                    ContractName     = fields[indices.MarketName].Trim(),
                    ReportDate       = DateTime.ParseExact(
                        fields[indices.ReportDate].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    CommercialLong   = ParseLong(fields[indices.CommLong]),
                    CommercialShort  = ParseLong(fields[indices.CommShort]),
                    NonCommercialLong  = ParseLong(fields[indices.NonCommLong]),
                    NonCommercialShort = ParseLong(fields[indices.NonCommShort]),
                    NonReportableLong  = ParseLong(fields[indices.NonRptLong]),
                    NonReportableShort = ParseLong(fields[indices.NonRptShort]),
                    OpenInterest       = ParseLong(fields[indices.OpenInterest])
                };

                records.Add(record);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CftcCOTDataFeed: failed to parse CSV row");
            }
        }

        return records;
    }

    /// <summary>
    /// Resolves CFTC legacy CSV column indices from the header row.
    /// The CFTC uses specific column names; this method matches them case-insensitively.
    /// </summary>
    private static bool TryGetColumnIndices(
        Dictionary<string, int> columnIndex, out CftcColumnIndices indices)
    {
        indices = default;

        // CFTC legacy report column name variants
        if (!TryFind(columnIndex, out int marketName,
                "Market_and_Exchange_Names", "Market and Exchange Names"))
            return false;
        // IMPORTANT: Match the yyyy-MM-dd columns FIRST. The CFTC CSV contains both
        // "As_of_Date_In_Form_YYMMDD" (values like "260401") and "Report_Date_as_YYYY-MM-DD"
        // (values like "2026-04-01"). We parse with "yyyy-MM-dd" format, so we must match
        // the yyyy-MM-dd column to avoid a format mismatch that silently drops all rows.
        if (!TryFind(columnIndex, out int reportDate,
                "Report_Date_as_YYYY-MM-DD", "As_of_Date_In_Form_YYYY-MM-DD",
                "As of Date in Form YYYY-MM-DD"))
            return false;
        if (!TryFind(columnIndex, out int nonCommLong,
                "NonComm_Positions_Long_All", "Noncommercial Positions-Long (All)",
                "NonComm_Positions_Long_Old"))
            return false;
        if (!TryFind(columnIndex, out int nonCommShort,
                "NonComm_Positions_Short_All", "Noncommercial Positions-Short (All)",
                "NonComm_Positions_Short_Old"))
            return false;
        if (!TryFind(columnIndex, out int commLong,
                "Comm_Positions_Long_All", "Commercial Positions-Long (All)",
                "Comm_Positions_Long_Old"))
            return false;
        if (!TryFind(columnIndex, out int commShort,
                "Comm_Positions_Short_All", "Commercial Positions-Short (All)",
                "Comm_Positions_Short_Old"))
            return false;
        if (!TryFind(columnIndex, out int nonRptLong,
                "NonRept_Positions_Long_All", "Nonreportable Positions-Long (All)",
                "NonRept_Positions_Long_Old"))
            return false;
        if (!TryFind(columnIndex, out int nonRptShort,
                "NonRept_Positions_Short_All", "Nonreportable Positions-Short (All)",
                "NonRept_Positions_Short_Old"))
            return false;
        if (!TryFind(columnIndex, out int openInterest,
                "Open_Interest_All", "Open Interest (All)",
                "Open_Interest_Old"))
            return false;

        indices = new CftcColumnIndices(
            marketName, reportDate, commLong, commShort,
            nonCommLong, nonCommShort, nonRptLong, nonRptShort, openInterest);

        return true;
    }

    private static bool TryFind(Dictionary<string, int> map, out int index, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out index))
                return true;
        }
        index = -1;
        return false;
    }

    /// <summary>
    /// Parses a CSV line, handling quoted fields that may contain commas.
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (line[i] == ',' && !inQuotes)
            {
                fields.Add(line[start..i].Trim('"', ' '));
                start = i + 1;
            }
        }
        fields.Add(line[start..].Trim('"', ' '));

        return fields.ToArray();
    }

    private static long ParseLong(string value)
    {
        var trimmed = value.Trim().Replace(",", "");
        return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    /// <summary>Parsed row from the CFTC legacy COT CSV.</summary>
    private class CftcCsvRecord
    {
        public string   ContractName       { get; init; } = string.Empty;
        public DateTime ReportDate         { get; init; }
        public long     CommercialLong     { get; init; }
        public long     CommercialShort    { get; init; }
        public long     NonCommercialLong  { get; init; }
        public long     NonCommercialShort { get; init; }
        public long     NonReportableLong  { get; init; }
        public long     NonReportableShort { get; init; }
        public long     OpenInterest       { get; init; }
    }

    /// <summary>Column index offsets for the CFTC CSV header row.</summary>
    private readonly record struct CftcColumnIndices(
        int MarketName, int ReportDate, int CommLong, int CommShort,
        int NonCommLong, int NonCommShort, int NonRptLong, int NonRptShort,
        int OpenInterest)
    {
        public int MaxIndex => new[]
        {
            MarketName, ReportDate, CommLong, CommShort,
            NonCommLong, NonCommShort, NonRptLong, NonRptShort, OpenInterest
        }.Max();
    }
}
