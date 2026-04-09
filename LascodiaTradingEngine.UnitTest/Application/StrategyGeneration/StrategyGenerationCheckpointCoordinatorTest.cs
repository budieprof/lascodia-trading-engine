using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

public class StrategyGenerationCheckpointCoordinatorTest
{
    [Fact]
    public async Task SaveAsync_WhenCheckpointPersists_UpdatesHealthState()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero));
        var healthStore = new StrategyGenerationHealthStore();
        var checkpointStore = new FakeCheckpointStore();
        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(new Mock<DbContext>().Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var coordinator = new StrategyGenerationCheckpointCoordinator(
            Mock.Of<ILogger<StrategyGenerationWorker>>(),
            checkpointStore,
            Mock.Of<IStrategyCandidateSelectionPolicy>(),
            Mock.Of<IStrategyParameterTemplateProvider>(),
            healthStore,
            timeProvider);

        await coordinator.SaveAsync(
            writeCtx.Object,
            "cycle-1",
            "fingerprint",
            new StrategyGenerationCheckpointProgressSnapshot(
                [],
                1,
                0,
                1,
                1,
                0,
                [],
                new Dictionary<string, int>(),
                new Dictionary<MarketRegime, int>(),
                new Dictionary<int, int>()),
            CancellationToken.None,
            "primary");

        var snapshot = healthStore.GetState();
        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, snapshot.LastCheckpointSavedAtUtc);
        Assert.Equal("primary", snapshot.LastCheckpointLabel);
        Assert.False(snapshot.IsCheckpointPersistenceDegraded);
        Assert.Equal(0, snapshot.ConsecutiveCheckpointSaveFailures);
    }

    [Fact]
    public async Task SaveAsync_WhenCheckpointPersistenceFails_MarksCheckpointHealthDegraded()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero));
        var healthStore = new StrategyGenerationHealthStore();
        var checkpointStore = new FakeCheckpointStore { SaveException = new InvalidOperationException("checkpoint write failed") };
        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(new Mock<DbContext>().Object);

        var coordinator = new StrategyGenerationCheckpointCoordinator(
            Mock.Of<ILogger<StrategyGenerationWorker>>(),
            checkpointStore,
            Mock.Of<IStrategyCandidateSelectionPolicy>(),
            Mock.Of<IStrategyParameterTemplateProvider>(),
            healthStore,
            timeProvider);

        await coordinator.SaveAsync(
            writeCtx.Object,
            "cycle-1",
            "fingerprint",
            new StrategyGenerationCheckpointProgressSnapshot(
                [],
                0,
                0,
                0,
                0,
                0,
                [],
                new Dictionary<string, int>(),
                new Dictionary<MarketRegime, int>(),
                new Dictionary<int, int>()),
            CancellationToken.None,
            "reserve");

        var snapshot = healthStore.GetState();
        Assert.True(snapshot.IsCheckpointPersistenceDegraded);
        Assert.Equal(1, snapshot.ConsecutiveCheckpointSaveFailures);
        Assert.Equal("reserve", snapshot.LastCheckpointLabel);
        Assert.Contains("checkpoint write failed", snapshot.LastCheckpointSaveFailureMessage);
    }

    private sealed class FakeCheckpointStore : IStrategyGenerationCheckpointStore
    {
        public Exception? SaveException { get; init; }

        public Task<GenerationCheckpointStore.State?> LoadCheckpointAsync(
            DbContext readDb,
            DateTime cycleDateUtc,
            string expectedFingerprint,
            CancellationToken ct)
            => Task.FromResult<GenerationCheckpointStore.State?>(null);

        public Task SaveCheckpointAsync(
            DbContext writeDb,
            string cycleId,
            GenerationCheckpointStore.State state,
            ILogger? logger,
            CancellationToken ct)
        {
            if (SaveException != null)
                throw SaveException;

            return Task.CompletedTask;
        }

        public Task ClearCheckpointAsync(DbContext writeDb, CancellationToken ct)
            => Task.CompletedTask;
    }
}
