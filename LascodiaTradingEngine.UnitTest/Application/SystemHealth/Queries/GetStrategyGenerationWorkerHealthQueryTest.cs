using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetStrategyGenerationWorkerHealth;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.SystemHealth.Queries;

public class GetStrategyGenerationWorkerHealthQueryTest
{
    [Fact]
    public async Task Handle_MapsTypedStateAndComputesAges()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(nowUtc);
        var healthStore = new StrategyGenerationHealthStore();
        healthStore.UpdateState(new StrategyGenerationHealthStateSnapshot
        {
            PendingArtifacts = 2,
            OldestPendingArtifactAttemptAtUtc = nowUtc.UtcDateTime.AddSeconds(-90),
            LastCheckpointSavedAtUtc = nowUtc.UtcDateTime.AddSeconds(-30),
            LastSkipReason = "lock_held",
            SummaryPublishFailures = 1,
            CapturedAtUtc = nowUtc.UtcDateTime,
        });
        healthStore.RecordPhaseFailure("cycle_summary_publish", "publish failed", nowUtc.UtcDateTime);

        var handler = new GetStrategyGenerationWorkerHealthQueryHandler(healthStore, timeProvider);

        var response = await handler.Handle(new GetStrategyGenerationWorkerHealthQuery(), CancellationToken.None);

        Assert.True(response.status);
        Assert.NotNull(response.data);
        Assert.Equal(2, response.data!.PendingArtifacts);
        Assert.Equal(90, response.data.OldestPendingArtifactAgeSeconds);
        Assert.Equal(30, response.data.CheckpointAgeSeconds);
        Assert.Equal("lock_held", response.data.LastSkipReason);
        Assert.Equal(1, response.data.SummaryPublishFailures);
        Assert.Single(response.data.PhaseStates);
    }
}
