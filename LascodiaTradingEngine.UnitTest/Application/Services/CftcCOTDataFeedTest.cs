using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using LascodiaTradingEngine.Application.Services.COTData;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class CftcCOTDataFeedTest : IDisposable
{
    private const string Header =
        "Market_and_Exchange_Names,Report_Date_as_YYYY-MM-DD,NonComm_Positions_Long_All,NonComm_Positions_Short_All,Comm_Positions_Long_All,Comm_Positions_Short_All,NonRept_Positions_Long_All,NonRept_Positions_Short_All,Open_Interest_All";

    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 16
    });

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task GetLatestPublishedReportAsync_ReturnsMostRecentMatchingRowAcrossCandidateYears()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero));
        var responses = new Dictionary<string, (HttpStatusCode Status, byte[]? Body)>
        {
            ["https://www.cftc.gov/files/dea/history/deacot2026.zip"] = (HttpStatusCode.OK, CreateZip(
                Header,
                "EURO FX - CHICAGO MERCANTILE EXCHANGE,2026-01-06,100,90,80,70,10,11,361",
                "EURO FX - CHICAGO MERCANTILE EXCHANGE,2026-04-21,140,95,75,65,12,9,396",
                "JAPANESE YEN - CHICAGO MERCANTILE EXCHANGE,2026-04-21,200,150,120,111,20,18,619")),
            ["https://www.cftc.gov/files/dea/history/deacot2025.zip"] = (HttpStatusCode.OK, CreateZip(
                Header,
                "EURO FX - CHICAGO MERCANTILE EXCHANGE,2025-12-30,90,80,60,55,8,7,300"))
        };

        var feed = CreateFeed(responses, timeProvider);

        var result = await feed.GetLatestPublishedReportAsync("EUR", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Unspecified), result!.ReportDate);
        Assert.Equal(75, result.CommercialLong);
        Assert.Equal(65, result.CommercialShort);
        Assert.Equal(140, result.NonCommercialLong);
        Assert.Equal(95, result.NonCommercialShort);
        Assert.Equal(12, result.RetailLong);
        Assert.Equal(9, result.RetailShort);
        Assert.Equal(396, result.TotalOpenInterest);
    }

    [Fact]
    public async Task GetLatestPublishedReportAsync_FallsBackToPreviousYear_WhenCurrentArchiveIsUnavailable()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero));
        var responses = new Dictionary<string, (HttpStatusCode Status, byte[]? Body)>
        {
            ["https://www.cftc.gov/files/dea/history/deacot2026.zip"] = (HttpStatusCode.NotFound, null),
            ["https://www.cftc.gov/files/dea/history/deacot2025.zip"] = (HttpStatusCode.OK, CreateZip(
                Header,
                "EURO FX - CHICAGO MERCANTILE EXCHANGE,2025-12-23,88,77,66,55,9,8,303",
                "EURO FX - CHICAGO MERCANTILE EXCHANGE,2025-12-30,98,79,69,58,10,9,323"))
        };

        var feed = CreateFeed(responses, timeProvider);

        var result = await feed.GetLatestPublishedReportAsync("EUR", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2025, 12, 30, 0, 0, 0, DateTimeKind.Unspecified), result!.ReportDate);
        Assert.Equal(69, result.CommercialLong);
        Assert.Equal(58, result.CommercialShort);
        Assert.Equal(98, result.NonCommercialLong);
        Assert.Equal(79, result.NonCommercialShort);
    }

    [Fact]
    public void SupportsCurrency_RecognizesKnownMappings_AndRejectsUnknownOnes()
    {
        var feed = CreateFeed(
            new Dictionary<string, (HttpStatusCode Status, byte[]? Body)>(),
            new TestTimeProvider(new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero)));

        Assert.True(feed.SupportsCurrency("EUR"));
        Assert.True(feed.SupportsCurrency("usd"));
        Assert.False(feed.SupportsCurrency("XAU"));
    }

    [Fact]
    public async Task GetLatestPublishedReportAsync_CoalescesConcurrentYearDownloads()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero));
        var responses = new Dictionary<string, (HttpStatusCode Status, byte[]? Body)>
        {
            ["https://www.cftc.gov/files/dea/history/deacot2026.zip"] = (HttpStatusCode.OK, CreateZip(
                Header,
                "EURO FX - CHICAGO MERCANTILE EXCHANGE,2026-04-21,140,95,75,65,12,9,396",
                "BRITISH POUND - CHICAGO MERCANTILE EXCHANGE,2026-04-21,130,85,71,61,14,11,381")),
            ["https://www.cftc.gov/files/dea/history/deacot2025.zip"] = (HttpStatusCode.OK, CreateZip(
                Header,
                "EURO FX - CHICAGO MERCANTILE EXCHANGE,2025-12-30,90,80,60,55,8,7,300",
                "BRITISH POUND - CHICAGO MERCANTILE EXCHANGE,2025-12-30,88,76,58,51,7,6,286"))
        };

        var handler = new FakeHttpMessageHandler(responses, TimeSpan.FromMilliseconds(50));
        var feed = CreateFeed(handler, timeProvider);

        await Task.WhenAll(
            feed.GetLatestPublishedReportAsync("EUR", CancellationToken.None),
            feed.GetLatestPublishedReportAsync("GBP", CancellationToken.None));

        Assert.Equal(1, handler.GetRequestCount("https://www.cftc.gov/files/dea/history/deacot2026.zip"));
        Assert.Equal(1, handler.GetRequestCount("https://www.cftc.gov/files/dea/history/deacot2025.zip"));
    }

    private CftcCOTDataFeed CreateFeed(
        Dictionary<string, (HttpStatusCode Status, byte[]? Body)> responses,
        TimeProvider timeProvider)
    {
        return CreateFeed(new FakeHttpMessageHandler(responses), timeProvider);
    }

    private CftcCOTDataFeed CreateFeed(
        FakeHttpMessageHandler handler,
        TimeProvider timeProvider)
    {
        var client = new HttpClient(handler);

        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient("CftcCOT"))
            .Returns(client);

        return new CftcCOTDataFeed(
            factory.Object,
            _cache,
            NullLogger<CftcCOTDataFeed>.Instance,
            timeProvider);
    }

    private static byte[] CreateZip(params string[] lines)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("annual.txt");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);

            foreach (var line in lines)
                writer.WriteLine(line);
        }

        return stream.ToArray();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, (HttpStatusCode Status, byte[]? Body)> _responses;
        private readonly ConcurrentDictionary<string, int> _requestCounts = new();
        private readonly TimeSpan _responseDelay;

        public FakeHttpMessageHandler(
            IReadOnlyDictionary<string, (HttpStatusCode Status, byte[]? Body)> responses,
            TimeSpan? responseDelay = null)
        {
            _responses = responses;
            _responseDelay = responseDelay ?? TimeSpan.Zero;
        }

        public int GetRequestCount(string url)
        {
            return _requestCounts.TryGetValue(url, out int count)
                ? count
                : 0;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            _requestCounts.AddOrUpdate(url, 1, static (_, count) => count + 1);

            if (_responseDelay > TimeSpan.Zero)
                await Task.Delay(_responseDelay, cancellationToken);

            if (!_responses.TryGetValue(url, out var response))
                response = (HttpStatusCode.NotFound, null);

            var message = new HttpResponseMessage(response.Status);
            if (response.Body != null)
                message.Content = new ByteArrayContent(response.Body);

            return message;
        }
    }
}
