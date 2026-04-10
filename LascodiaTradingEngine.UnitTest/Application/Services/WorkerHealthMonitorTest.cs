using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class WorkerHealthMonitorTest
{
    private readonly WorkerHealthMonitor _monitor;

    public WorkerHealthMonitorTest()
    {
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        _monitor = new WorkerHealthMonitor(
            mockScopeFactory.Object,
            Mock.Of<ILogger<WorkerHealthMonitor>>());
    }

    [Fact]
    public void RecordCycleSuccess_ResetsConsecutiveFailures()
    {
        // Record some failures first
        _monitor.RecordCycleFailure("TestWorker", "Error 1");
        _monitor.RecordCycleFailure("TestWorker", "Error 2");

        // Now record a success
        _monitor.RecordCycleSuccess("TestWorker", 100);

        var snapshots = _monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);

        Assert.Equal("TestWorker", snapshot.WorkerName);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.NotNull(snapshot.LastSuccessAt);
    }

    [Fact]
    public void RecordCycleFailure_IncrementsConsecutiveFailures()
    {
        _monitor.RecordCycleFailure("FailWorker", "Error 1");
        _monitor.RecordCycleFailure("FailWorker", "Error 2");
        _monitor.RecordCycleFailure("FailWorker", "Error 3");

        var snapshots = _monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);

        Assert.Equal("FailWorker", snapshot.WorkerName);
        Assert.Equal(3, snapshot.ConsecutiveFailures);
        Assert.Equal("Error 3", snapshot.LastErrorMessage);
        Assert.NotNull(snapshot.LastErrorAt);
    }

    [Fact]
    public void GetCurrentSnapshots_ReturnsDataForAllWorkers()
    {
        _monitor.RecordCycleSuccess("Worker1", 50);
        _monitor.RecordCycleSuccess("Worker2", 120);
        _monitor.RecordCycleFailure("Worker3", "Timeout");

        var snapshots = _monitor.GetCurrentSnapshots();

        Assert.Equal(3, snapshots.Count);
        Assert.Contains(snapshots, s => s.WorkerName == "Worker1");
        Assert.Contains(snapshots, s => s.WorkerName == "Worker2");
        Assert.Contains(snapshots, s => s.WorkerName == "Worker3");
    }

    [Fact]
    public void GetCurrentSnapshots_P50_P95_P99_ComputedCorrectly()
    {
        // Record many cycle durations to build up the sliding window
        var durations = new long[]
        {
            10, 20, 30, 40, 50, 60, 70, 80, 90, 100,
            110, 120, 130, 140, 150, 160, 170, 180, 190, 200
        };

        foreach (var d in durations)
        {
            _monitor.RecordCycleSuccess("PerfWorker", d);
        }

        var snapshots = _monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);

        // With 20 sorted values (10..200 step 10):
        // P50 index = floor(0.50 * 19) = 9 -> value at index 9 = 100
        Assert.Equal(100, snapshot.CycleDurationP50Ms);

        // Nearest-rank p95 for 20 samples points at the 19th value.
        Assert.Equal(190, snapshot.CycleDurationP95Ms);

        // Nearest-rank p99 for 20 samples points at the last value.
        Assert.Equal(200, snapshot.CycleDurationP99Ms);

        // Last duration should be the most recently enqueued
        Assert.Equal(200, snapshot.LastCycleDurationMs);
    }

    [Fact]
    public void GetCurrentSnapshots_UsesNearestRankForSmallSampleTailPercentiles()
    {
        _monitor.RecordCycleSuccess("SmallSampleWorker", 100);
        _monitor.RecordCycleSuccess("SmallSampleWorker", 200);

        var snapshots = _monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);

        Assert.Equal(100, snapshot.CycleDurationP50Ms);
        Assert.Equal(200, snapshot.CycleDurationP95Ms);
        Assert.Equal(200, snapshot.CycleDurationP99Ms);
    }

    [Fact]
    public void GetCurrentSnapshots_ReportsActualLastCycleDuration_NotMaxDuration()
    {
        _monitor.RecordCycleSuccess("LastDurationWorker", 400);
        _monitor.RecordCycleSuccess("LastDurationWorker", 150);

        var snapshots = _monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);

        Assert.Equal(150, snapshot.LastCycleDurationMs);
        Assert.Equal(400, snapshot.CycleDurationP95Ms);
    }

    [Fact]
    public void RecordWorkerMetadata_UpdatesConfiguredIntervalSeconds()
    {
        _monitor.RecordWorkerMetadata("MetadataWorker", "health-check", TimeSpan.FromSeconds(30));

        var snapshots = _monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);

        Assert.Equal("MetadataWorker", snapshot.WorkerName);
        Assert.Equal(30, snapshot.ConfiguredIntervalSeconds);
        Assert.True(snapshot.IsRunning);
    }

    [Fact]
    public void RecordCycleSuccess_AfterWorkerStopped_MarksWorkerRunningAgain()
    {
        _monitor.RecordWorkerStopped("RestartedWorker");

        _monitor.RecordCycleSuccess("RestartedWorker", 75);

        var snapshots = _monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);

        Assert.True(snapshot.IsRunning);
        Assert.NotNull(snapshot.LastSuccessAt);
    }

    [Fact]
    public void RecordWorkerHeartbeat_AfterWorkerStopped_MarksWorkerRunningAgain()
    {
        _monitor.RecordWorkerStopped("HeartbeatWorker");

        _monitor.RecordWorkerHeartbeat("HeartbeatWorker");

        var snapshots = _monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);

        Assert.True(snapshot.IsRunning);
        Assert.NotNull(snapshot.LastSuccessAt);
    }

    [Fact]
    public void RecordValidationTimingAndRecoveryMetrics_UpdatesSnapshot()
    {
        _monitor.RecordQueueLatency("ValidationWorker", 150);
        _monitor.RecordQueueLatency("ValidationWorker", 300);
        _monitor.RecordExecutionDuration("ValidationWorker", 800);
        _monitor.RecordExecutionDuration("ValidationWorker", 1200);
        _monitor.RecordRetry("ValidationWorker", 2);
        _monitor.RecordRecovery("ValidationWorker", 3);

        var snapshots = _monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);

        Assert.Equal(300, snapshot.LastQueueLatencyMs);
        Assert.Equal(150, snapshot.QueueLatencyP50Ms);
        Assert.Equal(300, snapshot.QueueLatencyP95Ms);
        Assert.Equal(1200, snapshot.LastExecutionDurationMs);
        Assert.Equal(800, snapshot.ExecutionDurationP50Ms);
        Assert.Equal(1200, snapshot.ExecutionDurationP95Ms);
        Assert.Equal(2, snapshot.RetriesLastHour);
        Assert.Equal(3, snapshot.RecoveriesLastHour);
    }
}
