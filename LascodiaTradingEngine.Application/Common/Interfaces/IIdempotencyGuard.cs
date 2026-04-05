namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Guards against duplicate EA command processing using stored idempotency keys.
/// Returns cached responses for already-processed requests.
/// </summary>
public record IdempotencyCheckResult(
    bool AlreadyProcessed,
    int? CachedStatusCode,
    string? CachedResponseJson);

public interface IIdempotencyGuard
{
    /// <summary>Checks whether a request with the given idempotency key has already been processed.</summary>
    Task<IdempotencyCheckResult> CheckAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Records a processed request's idempotency key and cached response for future deduplication.</summary>
    Task RecordAsync(
        string idempotencyKey,
        string endpoint,
        int statusCode,
        string responseJson,
        CancellationToken cancellationToken);
}
