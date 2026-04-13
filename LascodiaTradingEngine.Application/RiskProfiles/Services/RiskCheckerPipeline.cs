using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.RiskProfiles.Services;

/// <summary>
/// Composite risk checker that runs all registered <see cref="IRiskCheckStep"/> implementations
/// before delegating to the monolithic <see cref="RiskChecker"/>. Each step can independently
/// block a signal with a specific reason. If all steps pass, the full RiskChecker runs.
/// <para>
/// Includes a self-monitoring circuit breaker that triggers when the pipeline encounters
/// consecutive internal errors (DB timeouts, null references, etc.). The circuit breaker
/// is <b>fail-closed</b> — when open, all orders are rejected to prevent unvalidated trades.
/// It auto-resets after a configurable cooldown to allow probe attempts.
/// </para>
/// </summary>
public sealed class RiskCheckerPipeline : IRiskChecker
{
    private readonly IEnumerable<IRiskCheckStep> _steps;
    private readonly RiskChecker _innerChecker;
    private readonly ILogger<RiskCheckerPipeline> _logger;
    private readonly IAlertDispatcher _alertDispatcher;

    // ── Circuit breaker state (static — shared across all scoped instances) ──
    private static int _consecutiveInternalErrors;
    private static long _circuitOpenedAtUtcTicks;
    private const int MaxConsecutiveErrors = 5;
    private static readonly TimeSpan CircuitCooldown = TimeSpan.FromMinutes(2);

    public RiskCheckerPipeline(
        IEnumerable<IRiskCheckStep> steps,
        RiskChecker innerChecker,
        ILogger<RiskCheckerPipeline> logger,
        IAlertDispatcher alertDispatcher)
    {
        _steps = steps;
        _innerChecker = innerChecker;
        _logger = logger;
        _alertDispatcher = alertDispatcher;
    }

    public async Task<RiskCheckResult> CheckAsync(TradeSignal signal, RiskCheckContext context, CancellationToken ct)
    {
        // ── Circuit breaker guard ───────────────────────────────────────────
        int currentErrors = Volatile.Read(ref _consecutiveInternalErrors);
        if (currentErrors >= MaxConsecutiveErrors)
        {
            var circuitOpenedAt = new DateTime(Interlocked.Read(ref _circuitOpenedAtUtcTicks), DateTimeKind.Utc);
            if (DateTime.UtcNow - circuitOpenedAt < CircuitCooldown)
            {
                _logger.LogCritical(
                    "RiskChecker circuit OPEN — rejecting all orders (fail-closed). " +
                    "Manual intervention required. Consecutive errors: {Count}",
                    currentErrors);
                return new RiskCheckResult(
                    Passed: false,
                    BlockReason: "RiskChecker circuit breaker open — internal errors detected. " +
                                 "Orders blocked until resolved.");
            }

            // Cooldown elapsed — allow one probe attempt
            _logger.LogWarning(
                "RiskChecker circuit half-open — attempting probe (errors: {Count})",
                currentErrors);
        }

        try
        {
            // ── Run composable steps ────────────────────────────────────────
            foreach (var step in _steps)
            {
                try
                {
                    var result = await step.CheckAsync(signal, context, ct);
                    if (!result.Passed)
                    {
                        _logger.LogInformation(
                            "RiskCheckerPipeline: step {Step} blocked signal {SignalId} — {Reason}",
                            step.Name, signal.Id, result.BlockReason);
                        // Step failures are business logic, not internal errors — reset circuit
                        Interlocked.Exchange(ref _consecutiveInternalErrors, 0);
                        return new RiskCheckResult(Passed: false, BlockReason: result.BlockReason);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "RiskCheckerPipeline: step {Step} threw for signal {SignalId} — BLOCKING signal (fail-closed)",
                        step.Name, signal.Id);
                    await RecordInternalErrorAsync(ex, ct);
                    return new RiskCheckResult(
                        Passed: false,
                        BlockReason: $"Risk check step '{step.Name}' failed with error: {ex.Message}");
                }
            }

            // ── Delegate to inner checker ───────────────────────────────────
            var innerResult = await _innerChecker.CheckAsync(signal, context, ct);

            // Successful execution (pass or fail) — reset circuit breaker
            Interlocked.Exchange(ref _consecutiveInternalErrors, 0);
            return innerResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "RiskCheckerPipeline: inner checker threw for signal {SignalId} — BLOCKING signal (fail-closed)",
                signal.Id);
            await RecordInternalErrorAsync(ex, ct);
            return new RiskCheckResult(
                Passed: false,
                BlockReason: $"RiskChecker internal error: {ex.Message}");
        }
    }

    public Task<RiskCheckResult> CheckDrawdownAsync(
        RiskProfile profile,
        decimal currentBalance,
        decimal peakBalance,
        decimal dailyStartBalance,
        decimal maxAbsoluteDailyLoss,
        CancellationToken cancellationToken)
    {
        // Drawdown checks are account-level and don't go through the step pipeline.
        return _innerChecker.CheckDrawdownAsync(
            profile, currentBalance, peakBalance, dailyStartBalance, maxAbsoluteDailyLoss, cancellationToken);
    }

    /// <summary>
    /// Records an internal error in the circuit breaker state. If the error count reaches
    /// <see cref="MaxConsecutiveErrors"/>, opens the circuit and dispatches a critical alert.
    /// </summary>
    private async Task RecordInternalErrorAsync(Exception ex, CancellationToken ct)
    {
        int errors = Interlocked.Increment(ref _consecutiveInternalErrors);

        if (errors == MaxConsecutiveErrors)
        {
            Interlocked.Exchange(ref _circuitOpenedAtUtcTicks, DateTime.UtcNow.Ticks);
            _logger.LogCritical(ex,
                "RiskChecker circuit breaker OPENED after {Count} consecutive errors. " +
                "All orders will be rejected for {Cooldown} minutes.",
                errors, CircuitCooldown.TotalMinutes);

            await DispatchCircuitBreakerAlertAsync(ex.Message, ct);
        }
        else
        {
            _logger.LogError(ex,
                "RiskChecker internal error ({Count}/{Max})",
                errors, MaxConsecutiveErrors);
        }
    }

    /// <summary>
    /// Dispatches a critical alert when the circuit breaker opens. Best-effort — failures
    /// are logged but do not propagate (the circuit breaker itself must not throw).
    /// </summary>
    private async Task DispatchCircuitBreakerAlertAsync(string errorMessage, CancellationToken ct)
    {
        try
        {
            string message = $"RiskChecker circuit breaker opened after {MaxConsecutiveErrors} consecutive internal errors. " +
                             $"All new orders are blocked (fail-closed). Last error: {errorMessage}";

            var alert = new Alert
            {
                AlertType = AlertType.DrawdownBreached,
                Severity  = AlertSeverity.Critical,
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    source = "RiskCheckerCircuitBreaker",
                    consecutiveErrors = MaxConsecutiveErrors,
                    lastError = errorMessage,
                }),
                DeduplicationKey = "risk-checker-circuit-breaker",
                IsActive = true,
            };

            await _alertDispatcher.DispatchAsync(alert, message, ct);
        }
        catch (Exception alertEx)
        {
            // Alert dispatch failure must not mask the original circuit breaker event
            _logger.LogError(alertEx,
                "Failed to dispatch circuit breaker alert — alert system may also be degraded");
        }
    }
}
