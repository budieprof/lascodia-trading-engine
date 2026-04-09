using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

public class StrategyGenerationHealthStoreTest
{
    [Fact]
    public void UpdateState_RoundTripsSnapshot()
    {
        var store = new StrategyGenerationHealthStore();
        var nowUtc = new DateTime(2026, 4, 9, 12, 0, 0, DateTimeKind.Utc);

        store.UpdateState(new StrategyGenerationHealthStateSnapshot
        {
            PendingArtifacts = 3,
            LastSkipReason = "velocity_cap",
            CapturedAtUtc = nowUtc,
        });

        var snapshot = store.GetState();

        Assert.Equal(3, snapshot.PendingArtifacts);
        Assert.Equal("velocity_cap", snapshot.LastSkipReason);
        Assert.Equal(nowUtc, snapshot.CapturedAtUtc);
    }

    [Fact]
    public void RecordPhaseSuccess_AfterFailure_ResetsConsecutiveFailures()
    {
        var store = new StrategyGenerationHealthStore();
        var failureAt = new DateTime(2026, 4, 9, 12, 0, 0, DateTimeKind.Utc);
        var successAt = failureAt.AddMinutes(5);

        store.RecordPhaseFailure("checkpoint_save", "boom", failureAt);
        store.RecordPhaseSuccess("checkpoint_save", 42, successAt);

        var phase = Assert.Single(store.GetPhaseStates());
        Assert.Equal("checkpoint_save", phase.PhaseName);
        Assert.Equal(successAt, phase.LastSuccessAtUtc);
        Assert.Equal(0, phase.ConsecutiveFailures);
        Assert.Equal(42, phase.LastDurationMs);
    }
}
