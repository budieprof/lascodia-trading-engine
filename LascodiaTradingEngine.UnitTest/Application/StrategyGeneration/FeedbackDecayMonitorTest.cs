using LascodiaTradingEngine.Application.StrategyGeneration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

public class FeedbackDecayMonitorTest
{
    [Fact]
    public void GetEffectiveHalfLifeDays_ReturnsDefault_BeforeAnyEvaluation()
    {
        var monitor = new FeedbackDecayMonitor(NullLogger<FeedbackDecayMonitor>.Instance);

        Assert.Equal(FeedbackDecayMonitor.DefaultHalfLifeDays, monitor.GetEffectiveHalfLifeDays());
    }

    [Fact]
    public void GetEffectiveHalfLifeDays_IsThreadSafe()
    {
        var monitor = new FeedbackDecayMonitor(NullLogger<FeedbackDecayMonitor>.Instance);

        // Concurrent reads should not throw
        var tasks = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => monitor.GetEffectiveHalfLifeDays()))
            .ToArray();

        Task.WaitAll(tasks);
        Assert.All(tasks, t => Assert.Equal(FeedbackDecayMonitor.DefaultHalfLifeDays, t.Result));
    }

    [Fact]
    public void HalfLifeConstants_AreValid()
    {
        Assert.True(FeedbackDecayMonitor.MinHalfLifeDays > 0);
        Assert.True(FeedbackDecayMonitor.MaxHalfLifeDays > FeedbackDecayMonitor.MinHalfLifeDays);
        Assert.True(FeedbackDecayMonitor.DefaultHalfLifeDays >= FeedbackDecayMonitor.MinHalfLifeDays);
        Assert.True(FeedbackDecayMonitor.DefaultHalfLifeDays <= FeedbackDecayMonitor.MaxHalfLifeDays);
    }
}
