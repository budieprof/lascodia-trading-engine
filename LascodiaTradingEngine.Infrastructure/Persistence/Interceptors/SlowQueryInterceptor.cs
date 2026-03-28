using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Interceptors;

/// <summary>
/// EF Core interceptor that logs queries exceeding a configurable threshold.
/// Captures command text, parameters, and elapsed time for performance analysis.
/// </summary>
public class SlowQueryInterceptor : DbCommandInterceptor
{
    private readonly ILogger<SlowQueryInterceptor> _logger;
    private readonly int _thresholdMs;

    public SlowQueryInterceptor(ILogger<SlowQueryInterceptor> logger, int thresholdMs = 500)
    {
        _logger = logger;
        _thresholdMs = thresholdMs;
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        LogIfSlow(command, eventData.Duration);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData.Duration);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        LogIfSlow(command, eventData.Duration);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData.Duration);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        LogIfSlow(command, eventData.Duration);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData.Duration);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void LogIfSlow(DbCommand command, TimeSpan duration)
    {
        var elapsedMs = duration.TotalMilliseconds;
        if (elapsedMs < _thresholdMs)
            return;

        // Truncate very long SQL for log readability
        var sql = command.CommandText;
        if (sql.Length > 500)
            sql = sql[..500] + "... (truncated)";

        if (elapsedMs > 5000)
        {
            _logger.LogError("VERY SLOW QUERY ({ElapsedMs}ms > 5000ms threshold): {Sql}",
                (int)elapsedMs, sql);
        }
        else
        {
            _logger.LogWarning("Slow query ({ElapsedMs}ms > {ThresholdMs}ms threshold): {Sql}",
                (int)elapsedMs, _thresholdMs, sql);
        }
    }
}
