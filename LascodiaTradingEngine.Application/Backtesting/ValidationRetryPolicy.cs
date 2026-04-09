using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting;

internal readonly record struct ValidationFailureDecision(
    ValidationFailureCode FailureCode,
    bool IsTransient,
    string? FailureDetailsJson);

internal static class ValidationRetryPolicy
{
    internal static bool ShouldRetry(ValidationFailureDecision failure, int retryCount, int maxRetryAttempts)
    {
        if (retryCount >= maxRetryAttempts)
            return false;

        return failure.IsTransient;
    }

    internal static DateTime ComputeNextQueueTimeUtc(DateTime nowUtc, int retryCount, int baseBackoffSeconds)
    {
        int exponent = Math.Max(0, retryCount);
        double multiplier = Math.Pow(2d, exponent);
        double delaySeconds = Math.Max(5, baseBackoffSeconds) * multiplier;
        return nowUtc.AddSeconds(Math.Min(delaySeconds, 3600d));
    }

    internal static ValidationFailureDecision Classify(Exception ex) => ex switch
    {
        ValidationRunException validationEx => new ValidationFailureDecision(
            validationEx.FailureCode,
            validationEx.IsTransient,
            validationEx.FailureDetailsJson),
        JsonException jsonEx => new ValidationFailureDecision(
            ValidationRunFailureCodes.InvalidOptionsSnapshot,
            false,
            ValidationRunException.SerializeDetails(new
            {
                ExceptionType = jsonEx.GetType().Name,
                jsonEx.Message
            })),
        TimeoutException timeoutEx => new ValidationFailureDecision(
            ValidationRunFailureCodes.TransientInfrastructure,
            true,
            ValidationRunException.SerializeDetails(new
            {
                ExceptionType = timeoutEx.GetType().Name,
                timeoutEx.Message
            })),
        DbUpdateException dbEx => new ValidationFailureDecision(
            ValidationRunFailureCodes.TransientInfrastructure,
            true,
            ValidationRunException.SerializeDetails(new
            {
                ExceptionType = dbEx.GetType().Name,
                dbEx.Message
            })),
        _ => new ValidationFailureDecision(
            ValidationRunFailureCodes.ExecutionFailed,
            false,
            ValidationRunException.SerializeDetails(new
            {
                ExceptionType = ex.GetType().Name,
                ex.Message
            }))
    };

    internal static void RequeueBacktestRunForRetry(BacktestRun run, DateTime nowUtc, DateTime nextAvailableAtUtc)
    {
        BacktestRunStateMachine.Transition(run, RunStatus.Queued, nowUtc, resetQueuePosition: false, availableAtUtc: nextAvailableAtUtc);
        run.RetryCount++;
    }

    internal static void RequeueWalkForwardRunForRetry(WalkForwardRun run, DateTime nowUtc, DateTime nextAvailableAtUtc)
    {
        WalkForwardRunStateMachine.Transition(run, RunStatus.Queued, nowUtc, resetQueuePosition: false, availableAtUtc: nextAvailableAtUtc);
        run.RetryCount++;
    }
}
