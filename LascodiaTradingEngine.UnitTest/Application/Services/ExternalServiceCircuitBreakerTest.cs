using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class ExternalServiceCircuitBreakerTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ExternalServiceCircuitBreaker _breaker;

    public ExternalServiceCircuitBreakerTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
        _breaker = new ExternalServiceCircuitBreaker(_timeProvider, _metrics, Mock.Of<ILogger<ExternalServiceCircuitBreaker>>());
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public void IsOpen_UnknownService_ReturnsFalse()
    {
        Assert.False(_breaker.IsOpen("NeverSeen"));
    }

    [Fact]
    public void RecordFailure_UnderThreshold_LeavesCircuitClosed()
    {
        for (int i = 0; i < ExternalServiceCircuitBreaker.FailureThreshold - 1; i++)
            _breaker.RecordFailure("NewsFilter");

        Assert.False(_breaker.IsOpen("NewsFilter"));
    }

    [Fact]
    public void RecordFailure_AtThreshold_OpensCircuit()
    {
        for (int i = 0; i < ExternalServiceCircuitBreaker.FailureThreshold; i++)
            _breaker.RecordFailure("NewsFilter");

        Assert.True(_breaker.IsOpen("NewsFilter"));
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        for (int i = 0; i < ExternalServiceCircuitBreaker.FailureThreshold - 1; i++)
            _breaker.RecordFailure("NewsFilter");
        _breaker.RecordSuccess("NewsFilter");

        // Fresh streak of failures needed to open.
        for (int i = 0; i < ExternalServiceCircuitBreaker.FailureThreshold - 1; i++)
            _breaker.RecordFailure("NewsFilter");
        Assert.False(_breaker.IsOpen("NewsFilter"));
    }

    [Fact]
    public void Open_TransitionsToHalfOpen_AfterOpenDuration()
    {
        for (int i = 0; i < ExternalServiceCircuitBreaker.FailureThreshold; i++)
            _breaker.RecordFailure("MLSignalScorer");
        Assert.True(_breaker.IsOpen("MLSignalScorer"));

        _timeProvider.Advance(ExternalServiceCircuitBreaker.OpenDuration + TimeSpan.FromSeconds(1));

        // The first IsOpen call after the window elapses transitions Open -> HalfOpen
        // and lets the probe through (IsOpen returns false).
        Assert.False(_breaker.IsOpen("MLSignalScorer"));
    }

    [Fact]
    public void HalfOpen_FailedProbe_ReopensCircuit()
    {
        for (int i = 0; i < ExternalServiceCircuitBreaker.FailureThreshold; i++)
            _breaker.RecordFailure("MLSignalScorer");
        _timeProvider.Advance(ExternalServiceCircuitBreaker.OpenDuration + TimeSpan.FromSeconds(1));
        _ = _breaker.IsOpen("MLSignalScorer"); // triggers Open -> HalfOpen

        // A failure in HalfOpen reopens.
        _breaker.RecordFailure("MLSignalScorer");
        Assert.True(_breaker.IsOpen("MLSignalScorer"));
    }

    [Fact]
    public void HalfOpen_SuccessfulProbe_ClosesCircuit()
    {
        for (int i = 0; i < ExternalServiceCircuitBreaker.FailureThreshold; i++)
            _breaker.RecordFailure("RegimeCoherenceChecker");
        _timeProvider.Advance(ExternalServiceCircuitBreaker.OpenDuration + TimeSpan.FromSeconds(1));
        _ = _breaker.IsOpen("RegimeCoherenceChecker"); // Open -> HalfOpen

        _breaker.RecordSuccess("RegimeCoherenceChecker");
        Assert.False(_breaker.IsOpen("RegimeCoherenceChecker"));
    }

    [Fact]
    public void ForceOpen_SetsState_Immediately()
    {
        _breaker.ForceOpen("MLSignalScorer", TimeSpan.FromMinutes(5));
        Assert.True(_breaker.IsOpen("MLSignalScorer"));
    }

    [Fact]
    public void ServiceKeys_AreCaseInsensitive()
    {
        for (int i = 0; i < ExternalServiceCircuitBreaker.FailureThreshold; i++)
            _breaker.RecordFailure("newsfilter");
        Assert.True(_breaker.IsOpen("NewsFilter"));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 20, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
