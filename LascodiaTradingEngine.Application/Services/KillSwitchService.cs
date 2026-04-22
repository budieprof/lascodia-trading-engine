using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.EngineConfiguration.Commands.UpsertEngineConfig;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Default <see cref="IKillSwitchService"/> backed by <c>EngineConfig</c>.
/// Reads go through <see cref="EngineConfigCache"/> (hot-path safe), writes
/// dispatch <see cref="UpsertEngineConfigCommand"/> so the audit trail and
/// cache invalidation happen through the existing channel.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IKillSwitchService))]
public sealed class KillSwitchService : IKillSwitchService
{
    internal const string GlobalKey         = "KillSwitch:Global";
    internal const string StrategyKeyPrefix = "KillSwitch:Strategy:";

    // Kill switches are the last line of defence — multi-instance staleness of up to 30 s
    // meant an armed global kill could take half a minute to propagate to every process.
    // Dropped to 5 s: still cheap enough to keep the hot path fast (one DB read per 5 s per
    // key per instance) but tight enough that cross-instance propagation is well within the
    // EA's heartbeat SLA. Single-instance deploys see sub-tick propagation because
    // UpsertEngineConfigCommand calls EngineConfigCache.Invalidate directly.
    private const int CacheTtlSeconds = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EngineConfigCache _configCache;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<KillSwitchService> _logger;

    public KillSwitchService(
        IServiceScopeFactory scopeFactory,
        EngineConfigCache configCache,
        TradingMetrics metrics,
        ILogger<KillSwitchService> logger)
    {
        _scopeFactory = scopeFactory;
        _configCache = configCache;
        _metrics = metrics;
        _logger = logger;
    }

    public async ValueTask<bool> IsGlobalKilledAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        return await _configCache.GetBoolAsync(readCtx.GetDbContext(), GlobalKey, false, CacheTtlSeconds, ct);
    }

    public async ValueTask<bool> IsStrategyKilledAsync(long strategyId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        return await _configCache.GetBoolAsync(readCtx.GetDbContext(), StrategyKeyPrefix + strategyId, false, CacheTtlSeconds, ct);
    }

    public Task SetGlobalAsync(bool enabled, string reason, CancellationToken ct = default)
        => WriteSwitchAsync(GlobalKey, enabled, $"Global kill switch — {reason}", ct);

    public Task SetStrategyAsync(long strategyId, bool enabled, string reason, CancellationToken ct = default)
        => WriteSwitchAsync(StrategyKeyPrefix + strategyId, enabled, $"Strategy {strategyId} kill switch — {reason}", ct);

    private async Task WriteSwitchAsync(string key, bool enabled, string reason, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new UpsertEngineConfigCommand
        {
            Key             = key,
            Value           = enabled ? "true" : "false",
            DataType        = "Bool",
            IsHotReloadable = true,
            Description     = $"Kill switch — {reason}",
        }, ct);

        if (!result.status)
        {
            _logger.LogError(
                "KillSwitchService: failed to persist {Key}={Enabled}: {Message}",
                key, enabled, result.message);
            _metrics.WorkerErrors.Add(1,
                new KeyValuePair<string, object?>("worker", "KillSwitchService"),
                new KeyValuePair<string, object?>("reason", "persist_failed"));
            // Still record the decision so operators can trace the attempt.
        }

        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "EngineConfig",
            EntityId     = 0,
            DecisionType = "KillSwitch",
            Outcome      = enabled ? "Enabled" : "Disabled",
            Reason       = reason,
            Source       = nameof(KillSwitchService),
        }, ct);

        _logger.LogWarning(
            "KillSwitchService: {Key} set to {Enabled} ({Reason})", key, enabled, reason);
    }
}
