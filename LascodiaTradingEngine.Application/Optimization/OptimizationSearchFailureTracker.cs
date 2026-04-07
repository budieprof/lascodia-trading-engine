namespace LascodiaTradingEngine.Application.Optimization;

internal enum OptimizationSearchFailureKind
{
    Timeout,
    Exception
}

internal sealed class OptimizationSearchFailureTracker
{
    private const int RecentWindowSize = 24;
    private const int MinRecentSamples = 8;
    private const double RecentFailureRateThreshold = 0.75;

    private readonly object _gate = new();
    private readonly Queue<bool> _recentOutcomes = new();

    private int _successfulEvaluations;
    private int _failedEvaluations;
    private int _timedOutEvaluations;
    private int _exceptionFailures;
    private int _duplicateSuggestionsSkipped;
    private int _consecutiveFailures;
    private int _peakConsecutiveFailures;

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _successfulEvaluations++;
            _consecutiveFailures = 0;
            PushOutcome(success: true);
        }
    }

    public void RecordFailure(OptimizationSearchFailureKind failureKind)
    {
        lock (_gate)
        {
            _failedEvaluations++;
            _consecutiveFailures++;
            _peakConsecutiveFailures = Math.Max(_peakConsecutiveFailures, _consecutiveFailures);
            switch (failureKind)
            {
                case OptimizationSearchFailureKind.Timeout:
                    _timedOutEvaluations++;
                    break;
                case OptimizationSearchFailureKind.Exception:
                    _exceptionFailures++;
                    break;
            }

            PushOutcome(success: false);
        }
    }

    public void RecordDuplicateSuggestionSkip()
    {
        lock (_gate)
            _duplicateSuggestionsSkipped++;
    }

    public bool ShouldOpenCircuit(int consecutiveFailureThreshold)
    {
        lock (_gate)
        {
            return OptimizationSearchCoordinator.ShouldTripCircuitBreaker(
                _consecutiveFailures,
                consecutiveFailureThreshold,
                _recentOutcomes.Count,
                CalculateRecentFailureRateLocked(),
                MinRecentSamples,
                RecentFailureRateThreshold);
        }
    }

    public SearchExecutionSummary CreateSummary(string? abortReason, bool circuitBreakerTripped)
    {
        lock (_gate)
        {
            return new SearchExecutionSummary(
                abortReason,
                circuitBreakerTripped,
                _successfulEvaluations,
                _failedEvaluations,
                _timedOutEvaluations,
                _exceptionFailures,
                _duplicateSuggestionsSkipped,
                _peakConsecutiveFailures,
                _recentOutcomes.Count,
                CalculateRecentFailureRateLocked());
        }
    }

    private void PushOutcome(bool success)
    {
        _recentOutcomes.Enqueue(success);
        while (_recentOutcomes.Count > RecentWindowSize)
            _recentOutcomes.Dequeue();
    }

    private double CalculateRecentFailureRateLocked()
    {
        if (_recentOutcomes.Count == 0)
            return 0;

        int failures = _recentOutcomes.Count(outcome => !outcome);
        return (double)failures / _recentOutcomes.Count;
    }
}
