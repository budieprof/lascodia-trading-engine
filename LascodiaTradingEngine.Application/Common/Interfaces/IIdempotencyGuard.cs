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
    Task<IdempotencyCheckResult> CheckAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task RecordAsync(
        string idempotencyKey,
        string endpoint,
        int statusCode,
        string responseJson,
        CancellationToken cancellationToken);
}
