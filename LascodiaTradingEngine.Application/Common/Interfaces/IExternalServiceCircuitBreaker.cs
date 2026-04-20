namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Process-lifetime circuit breaker for external services consulted on the hot
/// tick path (news filter, ML scorer, regime coherence checker). Callers check
/// the state before invoking the service and record the outcome afterwards.
///
/// <para>
/// State machine:
/// <list type="bullet">
///   <item><b>Closed</b> — calls flow normally; failures accumulate.</item>
///   <item><b>Open</b>   — calls are short-circuited; the caller must use its
///         existing fail-closed path. Transitions to HalfOpen after
///         <c>OpenDurationSeconds</c>.</item>
///   <item><b>HalfOpen</b> — a single probe is allowed. Success closes the
///         circuit; failure re-opens it.</item>
/// </list>
/// </para>
///
/// <para>
/// Each service is identified by a free-form <paramref name="service"/> key
/// (e.g. "NewsFilter", "MLSignalScorer", "RegimeCoherenceChecker"). Keys are
/// case-insensitive.
/// </para>
/// </summary>
public interface IExternalServiceCircuitBreaker
{
    /// <summary>
    /// Returns <c>true</c> when the circuit is open and the caller should
    /// short-circuit without invoking the service. <c>false</c> when the
    /// caller should proceed (closed or half-open with probe allowed).
    /// </summary>
    bool IsOpen(string service);

    /// <summary>Records a successful call. Closes the circuit if it was half-open.</summary>
    void RecordSuccess(string service);

    /// <summary>
    /// Records a failed call. Increments the failure counter; when the
    /// consecutive-failure threshold is crossed, opens the circuit.
    /// </summary>
    void RecordFailure(string service);

    /// <summary>
    /// Test hook: forces a specific state. Never called from production
    /// paths; exists so operators can flip a circuit open manually without
    /// generating synthetic failures.
    /// </summary>
    void ForceOpen(string service, TimeSpan? duration = null);

    /// <summary>Test hook: resets a circuit to closed state.</summary>
    void Reset(string service);
}
