using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationDbExceptionClassifier
{
    private static readonly string[] FollowUpConstraintNames =
    [
        "IX_BacktestRun_SourceOptimizationRunId",
        "IX_WalkForwardRun_SourceOptimizationRunId"
    ];

    private static readonly string[] ActiveQueueConstraintNames =
    [
        "IX_OptimizationRun_ActivePerStrategy"
    ];

    internal static bool IsDuplicateFollowUpConstraintViolation(DbUpdateException ex)
        => IsUniqueConstraintViolation(
            ex,
            FollowUpConstraintNames,
            requiredMessageTokens: ["SourceOptimizationRunId"]);

    internal static bool IsActiveQueueConstraintViolation(DbUpdateException ex)
        => IsUniqueConstraintViolation(
            ex,
            ActiveQueueConstraintNames,
            requiredMessageTokens: ["OptimizationRun", "StrategyId"]);

    private static bool IsUniqueConstraintViolation(
        DbUpdateException ex,
        IReadOnlyList<string> constraintNames,
        IReadOnlyList<string> requiredMessageTokens)
    {
        if (TryMatchPostgresUniqueViolation(ex, constraintNames))
            return true;

        string message = ex.InnerException?.Message ?? ex.Message;

        foreach (var constraintName in constraintNames)
        {
            if (message.Contains(constraintName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            && requiredMessageTokens.All(token => message.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryMatchPostgresUniqueViolation(
        DbUpdateException ex,
        IReadOnlyList<string> constraintNames)
    {
        if (ex.InnerException is not PostgresException pgEx)
            return false;

        if (!string.Equals(pgEx.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
            return false;

        if (constraintNames.Count == 0)
            return true;

        return constraintNames.Any(
            constraintName => string.Equals(
                pgEx.ConstraintName,
                constraintName,
                StringComparison.OrdinalIgnoreCase));
    }
}
