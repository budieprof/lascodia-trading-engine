using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Daily evolutionary cycle: invoke <see cref="IEvolutionaryStrategyGenerator"/>,
/// persist surviving offspring as <see cref="StrategyStatus.Paused"/> + Draft
/// strategies for the existing screening pipeline to evaluate. Closes the loop
/// where the engine could only ever screen template-driven candidates by
/// continuously feeding mutated descendants of its best winners back into
/// generation.
///
/// <para>
/// Idempotent — duplicates (same parent, identical mutated parameters within the
/// dedup window) are dropped at insert time via the existing
/// <c>(Symbol, Timeframe, ParametersJson)</c> uniqueness behaviour.
/// </para>
/// </summary>
public sealed class EvolutionaryGeneratorWorker : BackgroundService
{
    private readonly ILogger<EvolutionaryGeneratorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private const string CK_Enabled            = "Evolution:Enabled";
    private const string CK_PollSeconds        = "Evolution:PollIntervalSeconds";
    private const string CK_MaxOffspring       = "Evolution:MaxOffspringPerCycle";

    private const int    DefaultPollSecs       = 86400; // daily
    private const int    DefaultMaxOffspring   = 12;

    public EvolutionaryGeneratorWorker(
        ILogger<EvolutionaryGeneratorWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EvolutionaryGeneratorWorker starting");
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSecs;
            try
            {
                pollSecs = await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EvolutionaryGeneratorWorker: cycle error");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<int> RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeCtx  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<IEvolutionaryStrategyGenerator>();
        var writeDb   = writeCtx.GetDbContext();
        var readDb    = readCtx.GetDbContext();

        bool enabled = await GetBoolAsync(readDb, CK_Enabled, defaultValue: true, ct);
        int pollSecs = await GetIntAsync (readDb, CK_PollSeconds,    DefaultPollSecs,     ct);
        int maxOff   = await GetIntAsync (readDb, CK_MaxOffspring,   DefaultMaxOffspring, ct);
        if (!enabled)
        {
            _logger.LogDebug("EvolutionaryGeneratorWorker: disabled via Evolution:Enabled=false");
            return pollSecs;
        }

        var offspring = await generator.ProposeOffspringAsync(maxOff, ct);
        if (offspring.Count == 0) return pollSecs;

        // Dedup: skip any offspring whose (Symbol, Timeframe, ParametersJson) collides with
        // an existing strategy. Cheap one-shot query against the candidates' triple set.
        var candidateKeys = offspring
            .Select(o => new { o.Symbol, o.Timeframe, o.ParametersJson })
            .Distinct()
            .ToList();
        var existingKeys = await writeDb.Set<Strategy>()
            .Where(s => !s.IsDeleted)
            .Select(s => new { s.Symbol, s.Timeframe, s.ParametersJson })
            .Where(s => candidateKeys.Contains(s))
            .ToListAsync(ct);
        var existingSet = existingKeys.Select(e => (e.Symbol, e.Timeframe, e.ParametersJson)).ToHashSet();

        int inserted = 0;
        var nowUtc = DateTime.UtcNow;
        foreach (var c in offspring)
        {
            if (existingSet.Contains((c.Symbol, c.Timeframe, c.ParametersJson))) continue;

            writeDb.Set<Strategy>().Add(new Strategy
            {
                Name                    = $"evo_{c.Symbol}_{c.Timeframe}_g{c.Generation}_{Guid.NewGuid():N}".Substring(0, 32),
                Description             = $"Evolutionary offspring of strategy {c.ParentStrategyId}: {c.MutationDescription}",
                Symbol                  = c.Symbol,
                Timeframe               = c.Timeframe,
                StrategyType            = c.StrategyType,
                ParametersJson          = c.ParametersJson,
                Status                  = StrategyStatus.Paused,
                LifecycleStage          = StrategyLifecycleStage.Draft,
                LifecycleStageEnteredAt = nowUtc,
                CreatedAt               = nowUtc,
                ParentStrategyId        = c.ParentStrategyId,
                Generation              = c.Generation,
            });
            inserted++;
        }

        if (inserted > 0)
        {
            await writeCtx.SaveChangesAsync(ct);
            _logger.LogInformation(
                "EvolutionaryGeneratorWorker: inserted {New} offspring strategies (proposed {Proposed}, deduped {Dedup})",
                inserted, offspring.Count, offspring.Count - inserted);
        }
        return pollSecs;
    }

    private static async Task<int> GetIntAsync(DbContext db, string key, int fallback, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted).Select(c => c.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static async Task<bool> GetBoolAsync(DbContext db, string key, bool defaultValue, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted).Select(c => c.Value).FirstOrDefaultAsync(ct);
        return bool.TryParse(raw, out var v) ? v : defaultValue;
    }
}
