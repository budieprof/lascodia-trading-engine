using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores processed idempotency keys to prevent duplicate processing of EA commands.
/// Keys have a 24-hour TTL and are pruned by the DataRetentionWorker.
/// The EA includes a unique <c>IdempotencyKey</c> GUID in each request; if the key
/// already exists in this table, the cached response is returned instead of re-processing.
/// </summary>
public class ProcessedIdempotencyKey : Entity<long>
{
    /// <summary>The idempotency key GUID sent by the EA in the request.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Name of the endpoint/command that processed this key.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>HTTP status code of the cached response.</summary>
    public int ResponseStatusCode { get; set; }

    /// <summary>JSON-serialised response body to return on duplicate requests.</summary>
    public string ResponseBodyJson { get; set; } = "{}";

    /// <summary>When this key was first processed.</summary>
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this key expires and can be pruned (default: ProcessedAt + 24h).</summary>
    public DateTime ExpiresAt { get; set; }

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
