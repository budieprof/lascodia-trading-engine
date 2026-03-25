using System.Diagnostics.Metrics;
using System.Reflection;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.EconomicEvents.Commands.CreateEconomicEvent;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class EconomicCalendarWorkerTest : IDisposable
{
    private readonly Mock<ILogger<EconomicCalendarWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IEconomicCalendarFeed> _mockCalendarFeed;
    private readonly EconomicCalendarOptions _options;
    private readonly TradingMetrics _metrics;
    private readonly TestMeterFactory _meterFactory;
    private readonly EconomicCalendarWorker _worker;

    public EconomicCalendarWorkerTest()
    {
        _mockLogger       = new Mock<ILogger<EconomicCalendarWorker>>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockMediator     = new Mock<IMediator>();
        _mockReadContext  = new Mock<IReadApplicationDbContext>();
        _mockCalendarFeed = new Mock<IEconomicCalendarFeed>();
        _meterFactory     = new TestMeterFactory();
        _metrics          = new TradingMetrics(_meterFactory);

        _options = new EconomicCalendarOptions
        {
            PollingIntervalHours          = 6,
            LookaheadDays                 = 7,
            ActualsPatchBatchSize         = 50,
            StaleEventCutoffDays          = 7,
            FeedCallTimeoutSeconds        = 30,
            FeedRetryCount                = 2,
            ActualsPatchRetryCount        = 1,
            ActualsPatchMaxConcurrency    = 5,
            SkipWeekends                  = false,
            FeedCircuitBreakerThreshold   = 3,
            SustainedEmptyFetchThreshold  = 3
        };

        // Wire up IServiceScopeFactory -> IServiceScope -> IServiceProvider
        var mockScope    = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        mockProvider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
        mockProvider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_mockReadContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IEconomicCalendarFeed))).Returns(_mockCalendarFeed.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        // Default: mediator returns success for CreateEconomicEventCommand
        _mockMediator
            .Setup(m => m.Send(It.IsAny<CreateEconomicEventCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        _worker = new EconomicCalendarWorker(
            _mockLogger.Object,
            _mockScopeFactory.Object,
            _options,
            _metrics);
    }

    public void Dispose()
    {
        _meterFactory.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupDbContext(List<CurrencyPair> currencyPairs, List<EconomicEvent> existingEvents)
    {
        var mockDbContext        = new Mock<DbContext>();
        var currencyPairDbSet   = currencyPairs.AsQueryable().BuildMockDbSet();
        var economicEventDbSet  = existingEvents.AsQueryable().BuildMockDbSet();

        mockDbContext.Setup(c => c.Set<CurrencyPair>()).Returns(currencyPairDbSet.Object);
        mockDbContext.Setup(c => c.Set<EconomicEvent>()).Returns(economicEventDbSet.Object);

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
    }

    /// <summary>
    /// Sets the private _consecutiveFeedFailures field via reflection.
    /// </summary>
    private void SetConsecutiveFeedFailures(long value)
    {
        var field = typeof(EconomicCalendarWorker)
            .GetField("_consecutiveFeedFailures", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(_worker, value);
    }

    /// <summary>
    /// Reads the private _consecutiveEmptyFetches field via reflection.
    /// </summary>
    private long GetConsecutiveEmptyFetches()
    {
        var field = typeof(EconomicCalendarWorker)
            .GetField("_consecutiveEmptyFetches", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (long)field.GetValue(_worker)!;
    }

    /// <summary>
    /// Invokes the private IngestUpcomingEventsAsync method directly.
    /// </summary>
    private async Task InvokeIngestUpcomingEventsAsync(CancellationToken ct = default)
    {
        var method = typeof(EconomicCalendarWorker)
            .GetMethod("IngestUpcomingEventsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_worker, new object[] { ct })!;
    }

    private static List<CurrencyPair> DefaultCurrencyPairs() => new()
    {
        new CurrencyPair
        {
            Id = 1, Symbol = "EURUSD", BaseCurrency = "EUR", QuoteCurrency = "USD",
            IsActive = true, IsDeleted = false
        }
    };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IngestUpcoming_CircuitBreakerOpen_SkipsIngestion()
    {
        // Arrange — set consecutive failures above the threshold (3)
        // and not on a probe cycle (failures=4, threshold=3, 4%3 != 0 => skip)
        SetConsecutiveFeedFailures(4);

        SetupDbContext(
            currencyPairs:  DefaultCurrencyPairs(),
            existingEvents: new List<EconomicEvent>());

        // Act
        await InvokeIngestUpcomingEventsAsync();

        // Assert — feed should not be called because circuit breaker is open
        _mockCalendarFeed.Verify(
            f => f.GetUpcomingEventsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert — no events created via mediator
        _mockMediator.Verify(
            m => m.Send(It.IsAny<CreateEconomicEventCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IngestUpcoming_NoCurrencyPairs_SkipsIngestion()
    {
        // Arrange — no active currency pairs in the database
        SetupDbContext(
            currencyPairs:  new List<CurrencyPair>(),
            existingEvents: new List<EconomicEvent>());

        // Act
        await InvokeIngestUpcomingEventsAsync();

        // Assert — feed should not be called because there are no currencies to query
        _mockCalendarFeed.Verify(
            f => f.GetUpcomingEventsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert — no events created
        _mockMediator.Verify(
            m => m.Send(It.IsAny<CreateEconomicEventCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IngestUpcoming_EmptyFeedResponse_IncrementsEmptyCounter()
    {
        // Arrange
        SetupDbContext(
            currencyPairs:  DefaultCurrencyPairs(),
            existingEvents: new List<EconomicEvent>());

        _mockCalendarFeed
            .Setup(f => f.GetUpcomingEventsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EconomicCalendarEvent>());

        long emptyBefore = GetConsecutiveEmptyFetches();

        // Act
        await InvokeIngestUpcomingEventsAsync();

        // Assert — empty fetch counter should have incremented
        long emptyAfter = GetConsecutiveEmptyFetches();
        Assert.Equal(emptyBefore + 1, emptyAfter);

        // Assert — no events created
        _mockMediator.Verify(
            m => m.Send(It.IsAny<CreateEconomicEventCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IngestUpcoming_NewEvent_CreatesViaMediator()
    {
        // Arrange
        var scheduledAt = DateTime.UtcNow.AddDays(1);

        SetupDbContext(
            currencyPairs:  DefaultCurrencyPairs(),
            existingEvents: new List<EconomicEvent>());

        var incomingEvents = new List<EconomicCalendarEvent>
        {
            new EconomicCalendarEvent(
                Title:       "US Non-Farm Payrolls",
                Currency:    "USD",
                Impact:      EconomicImpact.High,
                ScheduledAt: scheduledAt,
                Forecast:    "200K",
                Previous:    "187K",
                Actual:      null,
                ExternalKey: "nfp-2026-03",
                Source:      EconomicEventSource.ForexFactory)
        };

        _mockCalendarFeed
            .Setup(f => f.GetUpcomingEventsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(incomingEvents);

        // Act
        await InvokeIngestUpcomingEventsAsync();

        // Assert — CreateEconomicEventCommand sent for the new event
        _mockMediator.Verify(
            m => m.Send(
                It.Is<CreateEconomicEventCommand>(c =>
                    c.Title == "US Non-Farm Payrolls" &&
                    c.Currency == "USD" &&
                    c.ExternalKey == "nfp-2026-03"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task IngestUpcoming_DuplicateExternalKey_SkipsCreation()
    {
        // Arrange — an event with the same ExternalKey already exists in the database
        var scheduledAt = DateTime.UtcNow.AddDays(1);

        var existingEvent = new EconomicEvent
        {
            Id          = 1,
            Title       = "US Non-Farm Payrolls",
            Currency    = "USD",
            Impact      = EconomicImpact.High,
            ScheduledAt = scheduledAt,
            ExternalKey = "nfp-2026-03",
            IsDeleted   = false
        };

        SetupDbContext(
            currencyPairs:  DefaultCurrencyPairs(),
            existingEvents: new List<EconomicEvent> { existingEvent });

        var incomingEvents = new List<EconomicCalendarEvent>
        {
            new EconomicCalendarEvent(
                Title:       "US Non-Farm Payrolls",
                Currency:    "USD",
                Impact:      EconomicImpact.High,
                ScheduledAt: scheduledAt,
                Forecast:    "200K",
                Previous:    "187K",
                Actual:      null,
                ExternalKey: "nfp-2026-03",
                Source:      EconomicEventSource.ForexFactory)
        };

        _mockCalendarFeed
            .Setup(f => f.GetUpcomingEventsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(incomingEvents);

        // Act
        await InvokeIngestUpcomingEventsAsync();

        // Assert — no CreateEconomicEventCommand sent because the event is a duplicate
        _mockMediator.Verify(
            m => m.Send(It.IsAny<CreateEconomicEventCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test helper ──────────────────────────────────────────────────────────

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
                meter.Dispose();
        }
    }
}
