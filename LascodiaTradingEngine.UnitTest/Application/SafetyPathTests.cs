using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application;

/// <summary>
/// Tests for critical safety paths that protect the trading engine from
/// silent failures, non-deterministic behaviour, and data loss.
/// </summary>
public class SafetyPathTests
{
    // ─── WorkerHealthMonitor: RecordWorkerStopped ───────────────────────

    private static WorkerHealthMonitor CreateHealthMonitor()
    {
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        return new WorkerHealthMonitor(
            mockScopeFactory.Object,
            Mock.Of<ILogger<WorkerHealthMonitor>>());
    }

    [Fact]
    public void RecordWorkerStopped_SetsIsRunningToFalse()
    {
        // Arrange
        var monitor = CreateHealthMonitor();
        monitor.RecordCycleSuccess("TestWorker", 50);

        // Verify worker starts as running
        var before = monitor.GetCurrentSnapshots();
        Assert.Single(before);
        Assert.True(before[0].IsRunning);

        // Act
        monitor.RecordWorkerStopped("TestWorker");

        // Assert
        var after = monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(after);
        Assert.Equal("TestWorker", snapshot.WorkerName);
        Assert.False(snapshot.IsRunning);
    }

    [Fact]
    public void RecordWorkerStopped_WithReason_RecordsError()
    {
        // Arrange
        var monitor = CreateHealthMonitor();
        monitor.RecordCycleSuccess("CrashWorker", 100);

        // Act
        monitor.RecordWorkerStopped("CrashWorker", "OutOfMemoryException");

        // Assert
        var snapshots = monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);
        Assert.False(snapshot.IsRunning);
        Assert.Equal("OutOfMemoryException", snapshot.LastErrorMessage);
        Assert.NotNull(snapshot.LastErrorAt);
    }

    [Fact]
    public void RecordWorkerStopped_WithoutPriorRecord_CreatesStoppedEntry()
    {
        // Arrange — worker never recorded a cycle
        var monitor = CreateHealthMonitor();

        // Act
        monitor.RecordWorkerStopped("NeverStartedWorker");

        // Assert — entry exists and is marked stopped
        var snapshots = monitor.GetCurrentSnapshots();
        var snapshot = Assert.Single(snapshots);
        Assert.Equal("NeverStartedWorker", snapshot.WorkerName);
        Assert.False(snapshot.IsRunning);
        Assert.Null(snapshot.LastErrorMessage);
    }

    [Fact]
    public void RecordWorkerStopped_WithLongReason_TruncatesTo500()
    {
        // Arrange
        var monitor = CreateHealthMonitor();
        var longReason = new string('x', 1000);

        // Act
        monitor.RecordWorkerStopped("TruncWorker", longReason);

        // Assert
        var snapshot = Assert.Single(monitor.GetCurrentSnapshots());
        Assert.Equal(500, snapshot.LastErrorMessage!.Length);
    }

    // ─── SignalConflictResolver: deterministic tie-breaking ─────────────

    [Fact]
    public void Resolve_WithEqualScores_ReturnsDeterministicWinner()
    {
        // Arrange — two signals with identical scoring inputs but different strategy IDs.
        // The resolver breaks ties by ExpiresAt (ascending) then StrategyId (ascending),
        // so the signal with the lower ExpiresAt (or lower StrategyId if equal) should win.
        var resolver = new SignalConflictResolver(Mock.Of<ILogger<SignalConflictResolver>>());

        var expires = DateTime.UtcNow.AddMinutes(5);
        var signals = new List<PendingSignal>
        {
            new(
                StrategyId: 99,
                Symbol: "EURUSD",
                Timeframe: Timeframe.H1,
                StrategyType: StrategyType.BreakoutScalper,
                Direction: TradeDirection.Buy,
                EntryPrice: 1.1000m,
                StopLoss: 1.0950m,
                TakeProfit: 1.1100m,
                SuggestedLotSize: 0.1m,
                Confidence: 0.7m,
                MLConfidenceScore: 0.7m,
                MLModelId: 1,
                EstimatedCapacityLots: 10.0m,
                StrategySharpeRatio: 1.5m,
                ExpiresAt: expires),
            new(
                StrategyId: 42,
                Symbol: "EURUSD",
                Timeframe: Timeframe.H1,
                StrategyType: StrategyType.BreakoutScalper,
                Direction: TradeDirection.Buy,
                EntryPrice: 1.1000m,
                StopLoss: 1.0950m,
                TakeProfit: 1.1100m,
                SuggestedLotSize: 0.1m,
                Confidence: 0.7m,
                MLConfidenceScore: 0.7m,
                MLModelId: 1,
                EstimatedCapacityLots: 10.0m,
                StrategySharpeRatio: 1.5m,
                ExpiresAt: expires),
        };

        // Act — run multiple times to confirm determinism
        var results = Enumerable.Range(0, 10)
            .Select(_ => resolver.Resolve(signals))
            .ToList();

        // Assert — every run should produce the same winner
        var firstWinnerId = results[0][0].StrategyId;
        Assert.All(results, r =>
        {
            Assert.Single(r);
            Assert.Equal(firstWinnerId, r[0].StrategyId);
        });

        // The tie-breaker is ExpiresAt (same here) then StrategyId ascending, so 42 wins.
        Assert.Equal(42, firstWinnerId);
    }

    [Fact]
    public void Resolve_WithEqualScores_EarlierExpiry_Wins()
    {
        // Arrange — equal scores, but different expiry times
        var resolver = new SignalConflictResolver(Mock.Of<ILogger<SignalConflictResolver>>());

        var signals = new List<PendingSignal>
        {
            new(
                StrategyId: 1,
                Symbol: "GBPUSD",
                Timeframe: Timeframe.H1,
                StrategyType: StrategyType.BreakoutScalper,
                Direction: TradeDirection.Sell,
                EntryPrice: 1.2500m,
                StopLoss: 1.2550m,
                TakeProfit: 1.2400m,
                SuggestedLotSize: 0.1m,
                Confidence: 0.8m,
                MLConfidenceScore: 0.8m,
                MLModelId: 1,
                EstimatedCapacityLots: 10.0m,
                StrategySharpeRatio: 2.0m,
                ExpiresAt: DateTime.UtcNow.AddMinutes(10)),
            new(
                StrategyId: 2,
                Symbol: "GBPUSD",
                Timeframe: Timeframe.H1,
                StrategyType: StrategyType.BreakoutScalper,
                Direction: TradeDirection.Sell,
                EntryPrice: 1.2500m,
                StopLoss: 1.2550m,
                TakeProfit: 1.2400m,
                SuggestedLotSize: 0.1m,
                Confidence: 0.8m,
                MLConfidenceScore: 0.8m,
                MLModelId: 1,
                EstimatedCapacityLots: 10.0m,
                StrategySharpeRatio: 2.0m,
                ExpiresAt: DateTime.UtcNow.AddMinutes(3)),
        };

        // Act
        var result = resolver.Resolve(signals);

        // Assert — strategy 2 has earlier expiry, so it wins the tie-break
        Assert.Single(result);
        Assert.Equal(2, result[0].StrategyId);
    }

    // ─── QualityGateEvaluator: sentinel values disable gates ───────────

    [Fact]
    public void Evaluate_WithSentinelValues_DisablesGates()
    {
        // Arrange — set sentinel values that should disable their respective gates:
        //   MinBrierSkillScore <= -1.0  -> BSS gate disabled
        //   MaxEce <= 0                 -> ECE gate disabled
        //   MinF1Score <= 0             -> F1 gate disabled
        //   MinSharpeRatio <= 0         -> Sharpe gate disabled
        //   MinAccuracy <= 0            -> Accuracy gate disabled
        //   MinQualityRetentionRatio<=0 -> OOB regression gate disabled
        var input = new QualityGateEvaluator.QualityGateInput(
            Accuracy: 0.01,            // Terrible accuracy, but gate disabled
            ExpectedValue: 0.05,
            BrierScore: 0.20,
            SharpeRatio: -5.0,         // Terrible Sharpe, but gate disabled
            F1: 0.0,                   // Zero F1, but gate disabled
            OobAccuracy: 0.01,         // Terrible OOB, but regression gate disabled
            WfStdAccuracy: 0.03,
            Ece: 999.0,               // Absurd ECE, but gate disabled
            BrierSkillScore: -50.0,    // Terrible BSS, but gate disabled
            MinAccuracy: 0.0,          // sentinel: disables accuracy gate
            MinExpectedValue: 0.0,
            MaxBrierScore: 0.25,
            MinSharpeRatio: 0.0,       // sentinel: disables Sharpe gate
            MinF1Score: 0.0,           // sentinel: disables F1 gate
            MaxWfStdDev: 0.15,
            MaxEce: 0.0,              // sentinel: disables ECE gate
            MinBrierSkillScore: -1.0,  // sentinel: disables BSS gate
            MinQualityRetentionRatio: 0.0, // sentinel: disables OOB regression
            ParentOobAccuracy: 0.90,
            IsTrending: false,
            TrendingMinAccuracy: 0.65,
            TrendingMinEV: 0.02,
            EvBypassMinEV: 0.10,
            EvBypassMinSharpe: 0.50,
            BrierBypassMinEV: 0.10,
            BrierBypassMinSharpe: 1.0);

        // Act
        var result = QualityGateEvaluator.Evaluate(input);

        // Assert — despite terrible metrics, all sentinel-disabled gates pass
        Assert.True(result.Passed);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void Evaluate_WithOnlyBssSentinel_OtherGatesStillEnforced()
    {
        // Arrange — only BSS is sentineled; accuracy gate should still fail
        var input = new QualityGateEvaluator.QualityGateInput(
            Accuracy: 0.30,            // Below minimum
            ExpectedValue: 0.05,
            BrierScore: 0.20,
            SharpeRatio: 1.5,
            F1: 0.60,
            OobAccuracy: 0.62,
            WfStdAccuracy: 0.03,
            Ece: 0.08,
            BrierSkillScore: -50.0,    // Terrible, but gate disabled
            MinAccuracy: 0.55,
            MinExpectedValue: 0.0,
            MaxBrierScore: 0.25,
            MinSharpeRatio: 0.5,
            MinF1Score: 0.30,
            MaxWfStdDev: 0.15,
            MaxEce: 0.15,
            MinBrierSkillScore: -1.0,  // sentinel: disables BSS gate
            MinQualityRetentionRatio: 0.85,
            ParentOobAccuracy: 0.60,
            IsTrending: false,
            TrendingMinAccuracy: 0.65,
            TrendingMinEV: 0.02,
            EvBypassMinEV: 0.10,
            EvBypassMinSharpe: 0.50,
            BrierBypassMinEV: 0.10,
            BrierBypassMinSharpe: 1.0);

        // Act
        var result = QualityGateEvaluator.Evaluate(input);

        // Assert — accuracy gate should still catch the failure
        Assert.False(result.Passed);
        Assert.Contains("accuracy=", result.FailureReason);
        // BSS failure should NOT be in the reason since it's disabled
        Assert.DoesNotContain("bss=", result.FailureReason!);
    }

    // ─── DeadLetterSink: emergency buffer ──────────────────────────────

    [Fact]
    public void GetEmergencyBuffer_ReturnsBufferedEvents()
    {
        // Note: The emergency buffer is a static ConcurrentQueue, so we test
        // it by accessing the static method directly. The buffer is shared
        // across all DeadLetterSink instances.

        // Act — read the current buffer (may contain entries from other tests
        // since it's static, but the type and accessibility should work)
        var buffer = DeadLetterSink.GetEmergencyBuffer();

        // Assert — the method returns a non-null read-only list
        Assert.NotNull(buffer);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(buffer);
    }

    [Fact]
    public async Task WriteAsync_WhenBothDbAndFileFail_StoresInEmergencyBuffer()
    {
        // Arrange — mock scope factory so DB write throws, and use a logger
        // that we can verify. The file fallback will also fail because
        // we won't set up a valid directory (the test environment won't
        // have write access to the dead-letters path on all platforms).
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockServiceProvider = new Mock<IServiceProvider>();

        // Make the scope factory return a scope that throws when resolving IWriteApplicationDbContext
        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IWriteApplicationDbContext)))
            .Throws(new InvalidOperationException("DB unavailable"));
        mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        var sink = new DeadLetterSink(mockScopeFactory.Object, Mock.Of<ILogger<DeadLetterSink>>(), new TradingMetrics(new TestMeterFactory()));

        // Capture the buffer size before
        var beforeCount = DeadLetterSink.GetEmergencyBuffer().Count;

        // Act — this should fail DB write, then fail file write (invalid path/permissions),
        // and fall through to the emergency buffer
        var uniquePayload = $"{{\"test_event\":\"{Guid.NewGuid()}\"}}";
        await sink.WriteAsync(
            handlerName: "TestHandler",
            eventType: "TestEvent",
            eventPayloadJson: uniquePayload,
            errorMessage: "Original error",
            stackTrace: null,
            attempts: 1);

        // Assert — the emergency buffer should contain our payload
        var afterBuffer = DeadLetterSink.GetEmergencyBuffer();
        Assert.Contains(uniquePayload, afterBuffer);
    }
}

file class TestMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options) => new(options);
    public void Dispose() { }
}
