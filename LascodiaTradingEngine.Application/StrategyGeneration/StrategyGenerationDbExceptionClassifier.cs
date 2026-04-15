using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Classifies database uniqueness violations that are expected and recoverable in the
/// strategy-generation persistence path.
/// </summary>
internal static class StrategyGenerationDbExceptionClassifier
{
    private const string ActiveStrategyGenerationKeyConstraint = "IX_Strategy_ActiveGenerationKey";
    private const string ActiveValidationQueueKeyConstraint = "IX_BacktestRun_ActiveValidationQueueKey";

    internal static bool IsActiveStrategyDuplicateViolation(DbUpdateException ex)
        => IsUniqueConstraintViolation(
            ex,
            ActiveStrategyGenerationKeyConstraint,
            ["Strategy", "StrategyType", "Symbol", "Timeframe"]);

    internal static bool IsActiveValidationQueueDuplicateViolation(DbUpdateException ex)
        => IsUniqueConstraintViolation(
            ex,
            ActiveValidationQueueKeyConstraint,
            ["BacktestRun", "ValidationQueueKey"]);

    private static bool IsUniqueConstraintViolation(
        DbUpdateException ex,
        string constraintName,
        IReadOnlyList<string> requiredMessageTokens)
    {
        // Prefer structured PostgreSQL metadata when available, then fall back to message-token
        // matching so tests and provider differences can still classify likely duplicates.
        if (ex.InnerException is PostgresException pgEx
            && string.Equals(pgEx.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal)
            && string.Equals(pgEx.ConstraintName, constraintName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string message = ex.InnerException?.Message ?? ex.Message;
        if (message.Contains(constraintName, StringComparison.OrdinalIgnoreCase))
            return true;

        return message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            && requiredMessageTokens.All(token => message.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
