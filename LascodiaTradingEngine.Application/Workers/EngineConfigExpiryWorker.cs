using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically scans the <see cref="EngineConfig"/> table for entries whose value represents
/// an ISO-8601 datetime that has passed, and nullifies them. This prevents stale expiry-based
/// keys (e.g. <c>MLDrift:{Symbol}:{Tf}:AdwinDriftDetected</c>,
/// <c>MLCooldown:{Symbol}:{Tf}:ExpiresAt</c>) from accumulating indefinitely.
///
/// <para>Additionally, finds <c>MLMetrics:*:LastUpdated</c> entries older than 1 hour and
/// cleans up the associated metrics block by nullifying all keys sharing the same
/// <c>MLMetrics:{SymbolTf}</c> prefix.</para>
///
/// <para>Configuration keys (read from <see cref="EngineConfig"/>):</para>
/// <list type="bullet">
///   <item><c>EngineConfig:ExpiryPollIntervalSeconds</c> — default 21600 (6 hours)</item>
/// </list>
/// </summary>
public sealed class EngineConfigExpiryWorker : BackgroundService
{
    private const string CK_PollSecs = "EngineConfig:ExpiryPollIntervalSeconds";

    private const int DefaultPollIntervalSeconds = 21600; // 6 hours
    private static readonly TimeSpan MetricsStaleThreshold = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory                 _scopeFactory;
    private readonly ILogger<EngineConfigExpiryWorker>    _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new async DI scope per poll cycle so scoped EF Core
    /// contexts are correctly disposed after each expiry scan pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public EngineConfigExpiryWorker(
        IServiceScopeFactory              scopeFactory,
        ILogger<EngineConfigExpiryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Polls every <c>EngineConfig:ExpiryPollIntervalSeconds</c>
    /// seconds (default 21600 = 6 hours), reading the interval from <see cref="EngineConfig"/>
    /// on each cycle so it can be hot-reloaded without a restart.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EngineConfigExpiryWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollIntervalSeconds;

            try
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(stoppingToken);
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var readCtx  = readDb.GetDbContext();
                    var writeCtx = writeDb.GetDbContext();

                    pollSecs = await GetConfigAsync<int>(readCtx, CK_PollSecs, DefaultPollIntervalSeconds, stoppingToken);

                    int expiredCount = await CleanExpiredEntriesAsync(readCtx, writeCtx, stoppingToken);
                    int metricsCount = await CleanStaleMetricsBlocksAsync(readCtx, writeCtx, stoppingToken);

                    int total = expiredCount + metricsCount;
                    if (total > 0)
                    {
                        _logger.LogInformation(
                            "EngineConfigExpiryWorker: cleaned {Total} entries " +
                            "({Expired} expired datetime, {Metrics} stale metrics).",
                            total, expiredCount, metricsCount);
                    }
                    else
                    {
                        _logger.LogDebug("EngineConfigExpiryWorker: no expired entries found.");
                    }
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EngineConfigExpiryWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("EngineConfigExpiryWorker stopping.");
    }

    // ── Expired datetime entries ─────────────────────────────────────────────

    /// <summary>
    /// Finds all <see cref="EngineConfig"/> entries whose <see cref="EngineConfig.Value"/>
    /// is a valid ISO-8601 datetime that is in the past, and sets the value to <c>null</c>.
    /// </summary>
    /// <returns>The number of entries cleaned.</returns>
    private async Task<int> CleanExpiredEntriesAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        CancellationToken ct)
    {
        // Load all non-deleted entries with non-null values.
        // We parse in-memory because EF cannot evaluate DateTime.TryParse in SQL.
        var candidates = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Value != null && c.Value != "" && !c.IsDeleted)
            .Select(c => new { c.Id, c.Key, c.Value })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var expiredIds = new List<long>();

        foreach (var entry in candidates)
        {
            if (DateTime.TryParse(entry.Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsedDate))
            {
                if (parsedDate < now)
                {
                    expiredIds.Add(entry.Id);
                    _logger.LogDebug(
                        "EngineConfigExpiryWorker: expiring key '{Key}' (value={Value}, parsed={Parsed}).",
                        entry.Key, entry.Value, parsedDate);
                }
            }
        }

        if (expiredIds.Count == 0)
            return 0;

        // Soft-delete expired entries. Previously we nullified Value, but EngineConfig.Value
        // is NOT NULL (see EngineConfigConfiguration.cs) so that raised a 23502 constraint
        // violation every cycle and took the engine down. Soft-delete keeps the last known
        // value intact, honours the existing IsDeleted query filter, and matches how other
        // config rows are logically removed.
        int updated = await writeCtx.Set<EngineConfig>()
            .Where(c => expiredIds.Contains(c.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.IsDeleted, true)
                .SetProperty(c => c.LastUpdatedAt, now), ct);

        return updated;
    }

    // ── Stale MLMetrics block cleanup ────────────────────────────────────────

    /// <summary>
    /// Finds <c>MLMetrics:*:LastUpdated</c> entries whose datetime value is older than
    /// <see cref="MetricsStaleThreshold"/> and nullifies all keys sharing the same
    /// <c>MLMetrics:{SymbolTf}</c> prefix.
    /// </summary>
    /// <returns>The number of entries cleaned.</returns>
    private async Task<int> CleanStaleMetricsBlocksAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        CancellationToken ct)
    {
        // Find all MLMetrics:*:LastUpdated entries.
        var lastUpdatedEntries = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("MLMetrics:") &&
                        c.Key.EndsWith(":LastUpdated") &&
                        c.Value != null &&
                        !c.IsDeleted)
            .Select(c => new { c.Key, c.Value })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var stalePrefixes = new List<string>();

        foreach (var entry in lastUpdatedEntries)
        {
            if (DateTime.TryParse(entry.Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsedDate))
            {
                if (now - parsedDate > MetricsStaleThreshold)
                {
                    // Extract the prefix up to the last segment before ":LastUpdated".
                    // e.g. "MLMetrics:EURUSD:H1:LastUpdated" -> "MLMetrics:EURUSD:H1:"
                    int lastColon = entry.Key.LastIndexOf(":LastUpdated", StringComparison.Ordinal);
                    if (lastColon > 0)
                    {
                        string prefix = entry.Key[..lastColon] + ":";
                        stalePrefixes.Add(prefix);
                        _logger.LogDebug(
                            "EngineConfigExpiryWorker: metrics block '{Prefix}' is stale " +
                            "(LastUpdated={LastUpdated}, age={Age}).",
                            prefix, parsedDate, now - parsedDate);
                    }
                }
            }
        }

        if (stalePrefixes.Count == 0)
            return 0;

        // Nullify all keys matching the stale prefixes.
        int totalCleaned = 0;

        foreach (var prefix in stalePrefixes)
        {
            ct.ThrowIfCancellationRequested();

            // Soft-delete stale metrics blocks (Value is NOT NULL — see CleanExpiredEntriesAsync).
            int cleaned = await writeCtx.Set<EngineConfig>()
                .Where(c => c.Key.StartsWith(prefix) && !c.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.IsDeleted, true)
                    .SetProperty(c => c.LastUpdatedAt, now), ct);

            totalCleaned += cleaned;
        }

        return totalCleaned;
    }

    // ── Config helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table,
    /// falling back to <paramref name="defaultValue"/> if the key is absent or
    /// the stored value cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        DbContext         ctx,
        string            key,
        T                 defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
