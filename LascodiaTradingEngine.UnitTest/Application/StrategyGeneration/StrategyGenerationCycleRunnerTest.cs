using System.Diagnostics.Metrics;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

public class StrategyGenerationCycleRunnerTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();

    [Fact]
    public async Task RunAsync_WhenVelocityCapSkips_RecordsSkipReason()
    {
        var healthStore = new StrategyGenerationHealthStore();
        var runner = CreateVelocityCapRunner(
            healthStore,
            publishThrows: false,
            EventStateEnum.Published,
            out var cycleRunStore);

        await runner.RunAsync(CancellationToken.None);

        var snapshot = healthStore.GetState();
        Assert.Equal("velocity_cap", snapshot.LastSkipReason);
        Assert.NotNull(snapshot.LastSummaryPublishedAtUtc);
        Assert.Equal(1, cycleRunStore.CompleteCallCount);
    }

    [Fact]
    public async Task RunAsync_WhenCycleSummaryPublishFails_RecordsSummaryFailure()
    {
        var healthStore = new StrategyGenerationHealthStore();
        var runner = CreateVelocityCapRunner(
            healthStore,
            publishThrows: false,
            EventStateEnum.PublishedFailed,
            out var cycleRunStore);

        await runner.RunAsync(CancellationToken.None);

        var snapshot = healthStore.GetState();
        Assert.Equal(1, snapshot.SummaryPublishFailures);
        Assert.Contains("awaiting outbox publication", snapshot.LastSummaryPublishFailureMessage);
        Assert.Equal(1, cycleRunStore.SummaryFailureRecordCount);
    }

    public void Dispose()
    {
        _meterFactory.Dispose();
    }

    private StrategyGenerationCycleRunner CreateVelocityCapRunner(
        StrategyGenerationHealthStore healthStore,
        bool publishThrows,
        EventStateEnum summaryPublishState,
        out FakeCycleRunStore cycleRunStore)
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero));
        var readDbContext = new Mock<DbContext>().Object;
        var writeDbContext = new Mock<DbContext>().Object;
        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(readDbContext);
        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(writeDbContext);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var mediator = new Mock<IMediator>();
        var eventService = new Mock<IIntegrationEventService>();
        var eventLogReader = new FakeEventLogReader();
        if (publishThrows)
        {
            eventService
                .Setup(x => x.SaveAndPublish(It.IsAny<IDbContext>(), It.IsAny<Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>()))
                .ThrowsAsync(new InvalidOperationException("publish failed"));
        }
        else
        {
            eventService
                .Setup(x => x.SaveAndPublish(It.IsAny<IDbContext>(), It.IsAny<Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>()))
                .Callback<IDbContext, Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>((_, evt) =>
                    eventLogReader.SetStatus(evt.Id, summaryPublishState))
                .Returns(Task.CompletedTask);
        }

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(IReadApplicationDbContext))).Returns(readCtx.Object);
        serviceProvider.Setup(x => x.GetService(typeof(IWriteApplicationDbContext))).Returns(writeCtx.Object);
        serviceProvider.Setup(x => x.GetService(typeof(IMediator))).Returns(mediator.Object);
        serviceProvider.Setup(x => x.GetService(typeof(IIntegrationEventService))).Returns(eventService.Object);
        serviceProvider.Setup(x => x.GetService(typeof(IEventLogReader))).Returns(eventLogReader);
        serviceProvider.Setup(x => x.GetService(typeof(ISpreadProfileProvider))).Returns((object?)null);
        serviceProvider.Setup(x => x.GetService(typeof(ILivePerformanceBenchmark))).Returns((object?)null);
        serviceProvider.Setup(x => x.GetService(typeof(IPortfolioEquityCurveProvider))).Returns((object?)null);

        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(x => x.CreateScope()).Returns(scope.Object);

        var configProvider = new Mock<IStrategyGenerationConfigProvider>();
        configProvider
            .Setup(x => x.LoadAsync(It.IsAny<DbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StrategyGenerationConfigurationSnapshot(
                new GenerationConfig
                {
                    MaxCandidatesPerWeek = 1,
                },
                new Dictionary<string, string>(),
                new Dictionary<string, StrategyGenerationSymbolOverrides>()));

        var feedbackMonitor = new Mock<IFeedbackDecayMonitor>();
        feedbackMonitor.Setup(x => x.GetEffectiveHalfLifeDays()).Returns(30);

        var failureStore = new FakeFailureStore();
        cycleRunStore = new FakeCycleRunStore();
        var cycleDataService = new FakeCycleDataService();

        var runner = new StrategyGenerationCycleRunner(
            Mock.Of<ILogger<StrategyGenerationWorker>>(),
            scopeFactory.Object,
            Mock.Of<IRegimeStrategyMapper>(),
            new TradingMetrics(_meterFactory),
            feedbackMonitor.Object,
            configProvider.Object,
            new FakeCalendarPolicy(),
            cycleDataService,
            new FakeScreeningEngineFactory(),
            new FakeFeedbackCoordinator(),
            new FakeScreeningCoordinator(),
            new FakePersistenceCoordinator(),
            new FakePruningCoordinator(),
            cycleRunStore,
            failureStore,
            new FakeCheckpointStore(),
            new FakeCheckpointCoordinator(),
            healthStore,
            timeProvider);

        return runner;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, options.Scope);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
                meter.Dispose();
        }
    }

    private sealed class FakeCycleDataService : IStrategyGenerationCycleDataService
    {
        public Task<int> CountRecentAutoCandidatesAsync(DbContext db, DateTime createdAfterUtc, CancellationToken ct)
            => Task.FromResult(1);

        public Task<bool> IsInDrawdownRecoveryAsync(DbContext db, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<StrategyGenerationCycleDataSnapshot> LoadCycleDataAsync(DbContext db, GenerationConfig config, DateTime nowUtc, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class FakeFailureStore : IStrategyGenerationFailureStore
    {
        public Task<IReadOnlyList<LascodiaTradingEngine.Domain.Entities.StrategyGenerationFailure>> LoadUnreportedFailuresAsync(DbContext readDb, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LascodiaTradingEngine.Domain.Entities.StrategyGenerationFailure>>([]);

        public Task MarkFailuresReportedAsync(DbContext writeDb, IReadOnlyCollection<long> failureIds, CancellationToken ct)
            => Task.CompletedTask;

        public Task MarkFailuresResolvedAsync(DbContext writeDb, IReadOnlyCollection<string> candidateIds, CancellationToken ct)
            => Task.CompletedTask;

        public Task RecordFailuresAsync(DbContext writeDb, IReadOnlyCollection<StrategyGenerationFailureRecord> failures, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeCycleRunStore : IStrategyGenerationCycleRunStore
    {
        public int StartCallCount { get; private set; }
        public int CompleteCallCount { get; private set; }
        public int SummaryAttemptCount { get; private set; }
        public int SummaryPublishedCount { get; private set; }
        public int SummaryFailureRecordCount { get; private set; }

        public Task StartAsync(DbContext writeDb, string cycleId, string? fingerprint, CancellationToken ct)
        {
            StartCallCount++;
            return Task.CompletedTask;
        }

        public Task AttachFingerprintAsync(DbContext writeDb, string cycleId, string fingerprint, CancellationToken ct)
            => Task.CompletedTask;

        public Task StageCompletionAsync(
            DbContext writeDb,
            string cycleId,
            StrategyGenerationCycleRunCompletion completion,
            CancellationToken ct)
        {
            CompleteCallCount++;
            return Task.CompletedTask;
        }

        public Task StageSummaryDispatchAttemptAsync(
            DbContext writeDb,
            string cycleId,
            Guid eventId,
            string payloadJson,
            DateTime attemptedAtUtc,
            CancellationToken ct)
        {
            SummaryAttemptCount++;
            return Task.CompletedTask;
        }

        public Task MarkSummaryDispatchPublishedAsync(
            DbContext writeDb,
            string cycleId,
            DateTime dispatchedAtUtc,
            CancellationToken ct)
        {
            SummaryPublishedCount++;
            return Task.CompletedTask;
        }

        public Task RecordSummaryDispatchFailureAsync(
            DbContext writeDb,
            string cycleId,
            Guid eventId,
            string payloadJson,
            string errorMessage,
            DateTime failedAtUtc,
            CancellationToken ct)
        {
            SummaryFailureRecordCount++;
            return Task.CompletedTask;
        }

        public Task CompleteAsync(DbContext writeDb, string cycleId, StrategyGenerationCycleRunCompletion completion, CancellationToken ct)
            => Task.CompletedTask;

        public Task FailAsync(DbContext writeDb, string cycleId, string failureStage, string failureMessage, CancellationToken ct)
            => Task.CompletedTask;

        public Task<LascodiaTradingEngine.Domain.Entities.StrategyGenerationCycleRun?> LoadPreviousCompletedAsync(
            DbContext readDb,
            string currentCycleId,
            CancellationToken ct)
            => Task.FromResult<LascodiaTradingEngine.Domain.Entities.StrategyGenerationCycleRun?>(null);

        public Task<IReadOnlyList<StrategyGenerationSummaryDispatchRecord>> LoadPendingSummaryDispatchesAsync(
            DbContext readDb,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StrategyGenerationSummaryDispatchRecord>>([]);
    }

    private sealed class FakeCheckpointStore : IStrategyGenerationCheckpointStore
    {
        public Task<GenerationCheckpointStore.State?> LoadCheckpointAsync(DbContext readDb, DateTime cycleDateUtc, string expectedFingerprint, CancellationToken ct)
            => Task.FromResult<GenerationCheckpointStore.State?>(null);

        public Task SaveCheckpointAsync(DbContext writeDb, string cycleId, GenerationCheckpointStore.State state, ILogger? logger, CancellationToken ct)
            => Task.CompletedTask;

        public Task ClearCheckpointAsync(DbContext writeDb, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeEventLogReader : IEventLogReader
    {
        private readonly Dictionary<Guid, IntegrationEventStatusSnapshot> _statuses = [];

        public void SetStatus(Guid eventId, EventStateEnum state)
        {
            _statuses[eventId] = new IntegrationEventStatusSnapshot(
                eventId,
                state,
                state == EventStateEnum.Published ? 1 : 0,
                DateTime.UtcNow);
        }

        public Task<List<Lascodia.Trading.Engine.IntegrationEventLogEF.IntegrationEventLogEntry>> GetRetryableEventsAsync(
            TimeSpan stuckThreshold,
            int maxRetries,
            int batchSize,
            CancellationToken ct)
            => Task.FromResult(new List<Lascodia.Trading.Engine.IntegrationEventLogEF.IntegrationEventLogEntry>());

        public Task<IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot>> GetEventStatusSnapshotsAsync(
            IReadOnlyCollection<Guid> eventIds,
            CancellationToken ct)
        {
            IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot> result = _statuses
                .Where(kv => eventIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return Task.FromResult(result);
        }

        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeCheckpointCoordinator : IStrategyGenerationCheckpointCoordinator
    {
        public string ComputeFingerprint(StrategyGenerationScreeningContext context) => "fingerprint";

        public Task<StrategyGenerationCheckpointResumeState> RestoreAsync(
            DbContext db,
            StrategyGenerationScreeningContext context,
            Dictionary<string, int> candidatesPerCurrency,
            Dictionary<LascodiaTradingEngine.Domain.Enums.MarketRegime, int> regimeCandidatesCreated,
            CancellationToken ct)
            => throw new NotImplementedException();

        public Task SaveAsync(
            IWriteApplicationDbContext writeCtx,
            string cycleId,
            string checkpointFingerprint,
            StrategyGenerationCheckpointProgressSnapshot snapshot,
            CancellationToken ct,
            string checkpointLabel)
            => Task.CompletedTask;
    }

    private sealed class FakeCalendarPolicy : IStrategyGenerationCalendarPolicy
    {
        public bool IsWeekendForAssetMix(IEnumerable<(string Symbol, LascodiaTradingEngine.Domain.Entities.CurrencyPair? Pair)> symbols, DateTime utcNow)
            => false;

        public bool IsInBlackoutPeriod(string blackoutPeriods, string blackoutTimezone, DateTime utcNow)
            => false;
    }

    private sealed class FakeFeedbackCoordinator : IStrategyGenerationFeedbackCoordinator
    {
        public Task RefreshDynamicTemplatesAsync(DbContext db, CancellationToken ct) => Task.CompletedTask;

        public Task<(Dictionary<(LascodiaTradingEngine.Domain.Enums.StrategyType, LascodiaTradingEngine.Domain.Enums.MarketRegime, LascodiaTradingEngine.Domain.Enums.Timeframe), double> TypeRates, Dictionary<string, double> TemplateRates)> LoadPerformanceFeedbackAsync(
            DbContext db,
            IWriteApplicationDbContext writeCtx,
            double halfLifeDays,
            CancellationToken ct)
            => Task.FromResult((
                new Dictionary<(LascodiaTradingEngine.Domain.Enums.StrategyType, LascodiaTradingEngine.Domain.Enums.MarketRegime, LascodiaTradingEngine.Domain.Enums.Timeframe), double>(),
                new Dictionary<string, double>()));

        public IReadOnlyList<LascodiaTradingEngine.Domain.Enums.StrategyType> ApplyPerformanceFeedback(
            IReadOnlyList<LascodiaTradingEngine.Domain.Enums.StrategyType> types,
            LascodiaTradingEngine.Domain.Enums.MarketRegime regime,
            LascodiaTradingEngine.Domain.Enums.Timeframe timeframe,
            Dictionary<(LascodiaTradingEngine.Domain.Enums.StrategyType, LascodiaTradingEngine.Domain.Enums.MarketRegime, LascodiaTradingEngine.Domain.Enums.Timeframe), double> rates)
            => types;

        public void DetectFeedbackAdaptiveContradictions(
            IReadOnlyDictionary<(LascodiaTradingEngine.Domain.Enums.StrategyType, LascodiaTradingEngine.Domain.Enums.MarketRegime, LascodiaTradingEngine.Domain.Enums.Timeframe), double> feedbackRates,
            IReadOnlyDictionary<(LascodiaTradingEngine.Domain.Enums.MarketRegime, LascodiaTradingEngine.Domain.Enums.Timeframe), AdaptiveThresholdAdjustments> adaptiveAdjustmentsByContext)
        {
        }

        public Task<IReadOnlyDictionary<(LascodiaTradingEngine.Domain.Enums.MarketRegime, LascodiaTradingEngine.Domain.Enums.Timeframe), AdaptiveThresholdAdjustments>> ComputeAdaptiveThresholdsAsync(
            DbContext db,
            GenerationConfig config,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<(LascodiaTradingEngine.Domain.Enums.MarketRegime, LascodiaTradingEngine.Domain.Enums.Timeframe), AdaptiveThresholdAdjustments>>(
                new Dictionary<(LascodiaTradingEngine.Domain.Enums.MarketRegime, LascodiaTradingEngine.Domain.Enums.Timeframe), AdaptiveThresholdAdjustments>());
    }

    private sealed class FakeScreeningCoordinator : IStrategyGenerationScreeningCoordinator
    {
        public Dictionary<int, int> BuildInitialCorrelationGroupCounts(IReadOnlyList<string> activeSymbols) => [];

        public Task<StrategyGenerationScreeningResult> ScreenAllCandidatesAsync(
            DbContext db,
            IWriteApplicationDbContext writeCtx,
            StrategyGenerationScreeningContext context,
            CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class FakePersistenceCoordinator : IStrategyGenerationPersistenceCoordinator
    {
        public Task<PersistCandidatesResult> PersistCandidatesAsync(
            IReadApplicationDbContext readCtx,
            IWriteApplicationDbContext writeCtx,
            IIntegrationEventService eventService,
            ScreeningAuditLogger auditLogger,
            List<ScreeningOutcome> candidates,
            GenerationConfig config,
            CancellationToken ct)
            => throw new NotImplementedException();

        public Task ReplayPendingPostPersistArtifactsAsync(
            DbContext readDb,
            IWriteApplicationDbContext writeCtx,
            IIntegrationEventService eventService,
            ScreeningAuditLogger auditLogger,
            CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakePruningCoordinator : IStrategyGenerationPruningCoordinator
    {
        public Task<int> PruneStaleStrategiesAsync(
            IReadApplicationDbContext readCtx,
            IWriteApplicationDbContext writeCtx,
            ScreeningAuditLogger auditLogger,
            int pruneAfterFailed,
            CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class FakeScreeningEngineFactory : IStrategyScreeningEngineFactory
    {
        public StrategyScreeningEngine Create(Action<string>? onGateRejected = null)
            => throw new NotImplementedException();
    }
}
