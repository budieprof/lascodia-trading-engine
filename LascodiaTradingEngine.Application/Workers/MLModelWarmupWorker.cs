using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Polls for recently activated ML models and pre-loads their serialised snapshots
/// into <see cref="IMemoryCache"/>, eliminating cold-start latency on the first
/// inference request after promotion.
///
/// <para>
/// The <see cref="MLInferenceWarmupWorker"/> handles the one-time startup warm-up of
/// all active models. This worker complements it by continuously monitoring for newly
/// promoted models that appear after startup (e.g., when <see cref="MLShadowArbiterWorker"/>
/// promotes a challenger mid-session).
/// </para>
///
/// <para>
/// <b>Cache key pattern:</b> Uses the same <c>"MLSnapshot:{ModelId}"</c> key that
/// <see cref="MLSignalScorer"/> reads, so the scorer finds a pre-warmed snapshot
/// instead of deserialising on the first scoring call.
/// </para>
/// </summary>
public class MLModelWarmupWorker : BackgroundService
{
    private readonly ILogger<MLModelWarmupWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;

    /// <summary>
    /// Must match the cache key prefix used by <see cref="MLSignalScorer"/>.
    /// </summary>
    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";

    /// <summary>
    /// Must match the cache duration used by <see cref="MLSignalScorer"/>.
    /// </summary>
    private static readonly TimeSpan SnapshotCacheDuration = TimeSpan.FromMinutes(30);

    /// <summary>Default poll interval when not configured via EngineConfig.</summary>
    private const int DefaultPollIntervalSeconds = 30;

    /// <summary>
    /// Look-back window: only consider models activated within this duration.
    /// </summary>
    private static readonly TimeSpan RecentActivationWindow = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public MLModelWarmupWorker(
        ILogger<MLModelWarmupWorker> logger,
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _cache        = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay startup to let other services initialise
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        _logger.LogInformation("MLModelWarmupWorker: started — polling for recently activated models");

        while (!stoppingToken.IsCancellationRequested)
        {
            var pollInterval = DefaultPollIntervalSeconds;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var ctx = readCtx.GetDbContext();

                // Read configurable poll interval
                pollInterval = await GetConfigIntAsync(
                    ctx, "MLWarmup:PollIntervalSeconds", DefaultPollIntervalSeconds, stoppingToken);

                // Find models activated within the recent window that have serialised bytes
                var cutoff = DateTime.UtcNow.Subtract(RecentActivationWindow);

                var recentlyActivated = await ctx
                    .Set<MLModel>()
                    .Where(m => m.IsActive
                             && !m.IsDeleted
                             && m.ModelBytes != null
                             && m.ActivatedAt != null
                             && m.ActivatedAt > cutoff)
                    .Select(m => new { m.Id, m.Symbol, m.Timeframe, m.ModelBytes })
                    .ToListAsync(stoppingToken);

                foreach (var model in recentlyActivated)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    var cacheKey = $"{SnapshotCacheKeyPrefix}{model.Id}";

                    // Skip if already cached
                    if (_cache.TryGetValue(cacheKey, out _))
                        continue;

                    try
                    {
                        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(
                            model.ModelBytes!, JsonOptions);

                        if (snapshot is null)
                        {
                            _logger.LogWarning(
                                "MLModelWarmupWorker: failed to deserialise snapshot for model {Id}",
                                model.Id);
                            continue;
                        }

                        _cache.Set(cacheKey, snapshot, new MemoryCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = SnapshotCacheDuration
                        });

                        _logger.LogInformation(
                            "Pre-warmed model {Id} ({Symbol}/{Tf}) — snapshot cached",
                            model.Id, model.Symbol, model.Timeframe);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "MLModelWarmupWorker: failed to warm model {Id} ({Symbol}/{Tf})",
                            model.Id, model.Symbol, model.Timeframe);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLModelWarmupWorker: unhandled error in poll loop");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollInterval), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("MLModelWarmupWorker: stopped");
    }

    /// <summary>
    /// Reads an integer EngineConfig value, returning the default if not found.
    /// </summary>
    private static async Task<int> GetConfigIntAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx, string key,
        int defaultValue, CancellationToken ct)
    {
        try
        {
            var value = await ctx.Set<EngineConfig>()
                .Where(c => c.Key == key && !c.IsDeleted)
                .Select(c => c.Value)
                .FirstOrDefaultAsync(ct);

            return value is not null && int.TryParse(value, out var parsed) ? parsed : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }
}
