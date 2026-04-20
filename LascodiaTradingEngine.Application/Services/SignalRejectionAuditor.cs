using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Default <see cref="ISignalRejectionAuditor"/>. Resolves a fresh scoped
/// <c>IWriteApplicationDbContext</c> per call, persists one
/// <see cref="SignalRejectionAudit"/> row, emits the
/// <c>trading.signals.rejections_audited</c> counter, and swallows any write
/// failure with a warning log — audit failures must NEVER derail the caller's
/// primary rejection decision.
///
/// <para>
/// Registered as Singleton so callers don't have to worry about DI lifetime; the
/// auditor owns the scope boundary. This is safe because the only instance state
/// is the scope factory, metrics, and logger — all thread-safe.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(ISignalRejectionAuditor))]
public sealed class SignalRejectionAuditor : ISignalRejectionAuditor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<SignalRejectionAuditor> _logger;

    public SignalRejectionAuditor(
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        ILogger<SignalRejectionAuditor> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task RecordAsync(
        string stage,
        string reason,
        string symbol,
        string source,
        long strategyId = 0,
        long? tradeSignalId = null,
        string? detail = null,
        CancellationToken ct = default)
    {
        // Trim inputs defensively — the column lengths are narrow and an
        // out-of-bounds value should never crash the caller.
        stage  = Truncate(stage,  32)  ?? string.Empty;
        reason = Truncate(reason, 64)  ?? string.Empty;
        symbol = Truncate(symbol, 10)  ?? string.Empty;
        source = Truncate(source, 50)  ?? string.Empty;
        detail = Truncate(detail, 2000);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var db = writeCtx.GetDbContext();

            var row = new SignalRejectionAudit
            {
                TradeSignalId = tradeSignalId,
                StrategyId    = strategyId,
                Symbol        = symbol,
                Stage         = stage,
                Reason        = reason,
                Detail        = detail,
                Source        = source,
                RejectedAt    = DateTime.UtcNow,
            };

            db.Set<SignalRejectionAudit>().Add(row);
            await writeCtx.SaveChangesAsync(ct);

            _metrics.SignalRejectionsAudited.Add(1,
                new KeyValuePair<string, object?>("stage",  stage),
                new KeyValuePair<string, object?>("reason", reason));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller's cancellation — propagate.
            throw;
        }
        catch (Exception ex)
        {
            // Audit failure must not mask the caller's rejection decision; log
            // loudly so operators notice a broken audit pipeline but never
            // rethrow.
            _logger.LogWarning(ex,
                "SignalRejectionAuditor: failed to persist audit row for stage={Stage} reason={Reason} symbol={Symbol} source={Source}",
                stage, reason, symbol, source);
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null) return null;
        return value.Length <= max ? value : value[..max];
    }
}
