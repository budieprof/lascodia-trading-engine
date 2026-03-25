using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Dead-letter sink with automatic fallback. Tries the database first; if the DB write
/// fails (e.g. connection lost, timeout), writes a JSON file to the local filesystem so
/// the event is preserved for manual replay once the DB recovers.
///
/// File fallback path: <c>{BaseDirectory}/dead-letters/{yyyy-MM-dd}/{HandlerName}_{timestamp}_{guid}.json</c>
/// </summary>
[RegisterService(ServiceLifetime.Scoped)]
public class DeadLetterSink : IDeadLetterSink
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeadLetterSink> _logger;

    private static readonly string FallbackDirectory =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dead-letters");

    public DeadLetterSink(IServiceScopeFactory scopeFactory, ILogger<DeadLetterSink> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task WriteAsync(
        string handlerName,
        string eventType,
        string eventPayloadJson,
        string errorMessage,
        string? stackTrace,
        int attempts,
        CancellationToken ct = default)
    {
        // ── Primary: write to database ──────────────────────────────────────
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var db = writeContext.GetDbContext();

            await db.Set<DeadLetterEvent>().AddAsync(new DeadLetterEvent
            {
                HandlerName    = handlerName,
                EventType      = eventType,
                EventPayload   = eventPayloadJson,
                ErrorMessage   = errorMessage,
                StackTrace     = stackTrace,
                Attempts       = attempts,
                DeadLetteredAt = DateTime.UtcNow,
            }, ct);

            await writeContext.SaveChangesAsync(ct);

            _logger.LogWarning(
                "DeadLetterSink: persisted dead-letter to DB (handler={Handler}, event={EventType})",
                handlerName, eventType);
            return;
        }
        catch (Exception dbEx)
        {
            _logger.LogError(dbEx,
                "DeadLetterSink: DB write failed for {Handler}/{EventType} — falling back to file",
                handlerName, eventType);
        }

        // ── Fallback: write to local JSON file ──────────────────────────────
        try
        {
            var dayDir = Path.Combine(FallbackDirectory, DateTime.UtcNow.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dayDir);

            var fileName = $"{handlerName}_{DateTime.UtcNow:HHmmss_fff}_{Guid.NewGuid():N}.json";
            var filePath = Path.Combine(dayDir, fileName);

            var payload = new
            {
                HandlerName    = handlerName,
                EventType      = eventType,
                EventPayload   = eventPayloadJson,
                ErrorMessage   = errorMessage,
                StackTrace     = stackTrace,
                Attempts       = attempts,
                DeadLetteredAt = DateTime.UtcNow,
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json, ct);

            _logger.LogWarning(
                "DeadLetterSink: DB unavailable — dead-letter written to file {FilePath}",
                filePath);
        }
        catch (Exception fileEx)
        {
            // Last resort: log the full payload at Critical so it exists in structured logs
            _logger.LogCritical(fileEx,
                "DeadLetterSink: BOTH DB and file fallback failed for {Handler}/{EventType}. " +
                "Event payload: {Payload}. Original error: {OriginalError}",
                handlerName, eventType, eventPayloadJson, errorMessage);
        }
    }
}
