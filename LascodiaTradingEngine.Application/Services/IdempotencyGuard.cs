using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Guards against duplicate EA command processing by storing processed idempotency keys.
/// Returns cached responses for already-processed requests. Keys expire after 24 hours.
/// </summary>
[RegisterService]
public class IdempotencyGuard : IIdempotencyGuard
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly DataRetentionOptions _options;
    private readonly ILogger<IdempotencyGuard> _logger;

    public IdempotencyGuard(
        IReadApplicationDbContext readContext,
        IWriteApplicationDbContext writeContext,
        DataRetentionOptions options,
        ILogger<IdempotencyGuard> logger)
    {
        _readContext   = readContext;
        _writeContext  = writeContext;
        _options       = options;
        _logger        = logger;
    }

    public async Task<IdempotencyCheckResult> CheckAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
            return new IdempotencyCheckResult(false, null, null);

        // Use write context for the duplicate check to avoid read-replica lag.
        // When the read DB is a replica, a recently inserted key may not be visible yet,
        // allowing a duplicate request to pass through.
        var existing = await _writeContext.GetDbContext()
            .Set<ProcessedIdempotencyKey>()
            .FirstOrDefaultAsync(k => k.Key == idempotencyKey && !k.IsDeleted, cancellationToken);

        if (existing is null)
            return new IdempotencyCheckResult(false, null, null);

        // Check if expired — soft-delete via write context
        if (existing.ExpiresAt < DateTime.UtcNow)
        {
            var writeEntry = await _writeContext.GetDbContext()
                .Set<ProcessedIdempotencyKey>()
                .FirstOrDefaultAsync(k => k.Id == existing.Id, cancellationToken);
            if (writeEntry is not null)
            {
                writeEntry.IsDeleted = true;
                await _writeContext.GetDbContext().SaveChangesAsync(cancellationToken);
            }
            return new IdempotencyCheckResult(false, null, null);
        }

        _logger.LogDebug("Idempotency: duplicate request detected for key {Key}", idempotencyKey);
        return new IdempotencyCheckResult(true, existing.ResponseStatusCode, existing.ResponseBodyJson);
    }

    public async Task RecordAsync(
        string idempotencyKey,
        string endpoint,
        int statusCode,
        string responseJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(idempotencyKey)) return;

        var record = new ProcessedIdempotencyKey
        {
            Key                = idempotencyKey,
            Endpoint           = endpoint,
            ResponseStatusCode = statusCode,
            ResponseBodyJson   = responseJson,
            ProcessedAt        = DateTime.UtcNow,
            ExpiresAt          = DateTime.UtcNow.AddHours(_options.IdempotencyKeyTtlHours)
        };

        await _writeContext.GetDbContext().Set<ProcessedIdempotencyKey>().AddAsync(record, cancellationToken);
        await _writeContext.GetDbContext().SaveChangesAsync(cancellationToken);
    }
}
