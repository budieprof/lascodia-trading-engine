using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration.Commands.TriggerStrategyGenerationCycle;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Triggers a manual strategy-generation cycle immediately, bypassing the normal
/// time-of-day schedule gate. Flows through <see cref="IStrategyGenerationScheduler.ExecuteManualRunAsync"/>
/// so it still obeys the distributed generation lock, circuit breaker, and cycle-run
/// bookkeeping — an operator cannot stack two cycles on top of each other by calling
/// the endpoint twice.
///
/// <para>
/// Typical use cases: (a) testing a recently-deployed generation change without waiting
/// for the 02:12 UTC schedule window, (b) re-running a failed cycle after fixing a
/// transient dependency, (c) validating the chicken-and-egg CompositeML deferral path
/// end-to-end during development.
/// </para>
///
/// <para>
/// The command fires the cycle on the current thread and awaits its completion. For
/// large generation windows this can take tens of seconds; callers should use a
/// sufficient HTTP timeout or make the request in the background.
/// </para>
/// </summary>
public class TriggerStrategyGenerationCycleCommand : IRequest<ResponseData<string>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves the scheduler and cycle runner from a fresh DI scope (so they receive their
/// own scoped dependency graphs rather than sharing MediatR's pipeline scope), then calls
/// <see cref="IStrategyGenerationScheduler.ExecuteManualRunAsync"/> with the cycle runner's
/// <c>RunAsync</c> as the callback. Exactly the same path the hosted worker uses for its
/// internal manual-run hook.
/// </summary>
public class TriggerStrategyGenerationCycleCommandHandler
    : IRequestHandler<TriggerStrategyGenerationCycleCommand, ResponseData<string>>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TriggerStrategyGenerationCycleCommandHandler> _logger;

    public TriggerStrategyGenerationCycleCommandHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<TriggerStrategyGenerationCycleCommandHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ResponseData<string>> Handle(
        TriggerStrategyGenerationCycleCommand request,
        CancellationToken ct)
    {
        _logger.LogInformation("TriggerStrategyGenerationCycleCommand: manual cycle requested");

        using var schedulerScope = _scopeFactory.CreateScope();
        var scheduler = schedulerScope.ServiceProvider.GetRequiredService<IStrategyGenerationScheduler>();

        try
        {
            await scheduler.ExecuteManualRunAsync(async innerCt =>
            {
                // Fresh scope for the heavyweight cycle body, mirroring the
                // StrategyGenerationWorker.RunGenerationCycleCoreAsync pattern.
                using var cycleScope = _scopeFactory.CreateScope();
                var cycleRunner = cycleScope.ServiceProvider.GetRequiredService<IStrategyGenerationCycleRunner>();
                await cycleRunner.RunAsync(innerCt);
            }, ct);

            _logger.LogInformation("TriggerStrategyGenerationCycleCommand: manual cycle completed");
            return ResponseData<string>.Init("Manual strategy-generation cycle completed.", true, "Successful", "00");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TriggerStrategyGenerationCycleCommand: manual cycle faulted");
            return ResponseData<string>.Init(null, false, $"Cycle faulted: {ex.Message}", "-11");
        }
    }
}
