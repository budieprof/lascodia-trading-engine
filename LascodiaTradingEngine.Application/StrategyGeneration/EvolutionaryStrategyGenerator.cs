using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Single offspring proposed by <see cref="IEvolutionaryStrategyGenerator"/>.
/// The caller persists it (after screening) with <see cref="ParentStrategyId"/>
/// linking back to the source strategy so the lineage tree is queryable.
/// </summary>
public sealed record EvolutionaryCandidate(
    long           ParentStrategyId,
    int            Generation,
    string         Symbol,
    Timeframe      Timeframe,
    StrategyType   StrategyType,
    string         ParametersJson,
    string         MutationDescription);

public interface IEvolutionaryStrategyGenerator
{
    /// <summary>
    /// Proposes mutated offspring of the highest-Sharpe Approved/Active strategies
    /// up to <paramref name="maxOffspring"/> total. Each candidate is a new
    /// numeric-parameter perturbation (no archetype change) within the parent's
    /// existing parameter bounds. Caller is responsible for screening + persisting
    /// surviving candidates as new <see cref="Strategy"/> rows.
    /// </summary>
    Task<IReadOnlyList<EvolutionaryCandidate>> ProposeOffspringAsync(
        int maxOffspring, CancellationToken ct);
}

[RegisterService(ServiceLifetime.Scoped, typeof(IEvolutionaryStrategyGenerator))]
public sealed class EvolutionaryStrategyGenerator : IEvolutionaryStrategyGenerator
{
    private readonly IReadApplicationDbContext _readCtx;
    private readonly ILogger<EvolutionaryStrategyGenerator> _logger;

    private const int     MaxGenerationDepth          = 5;
    private const decimal MinParentSharpe             = 0.5m;
    private const int     ParentPoolSize              = 8;
    private const int     OffspringPerParent          = 3;
    private const double  MutationStrength            = 0.15;   // ±15% Gaussian-ish perturbation

    public EvolutionaryStrategyGenerator(
        IReadApplicationDbContext readCtx, ILogger<EvolutionaryStrategyGenerator> logger)
    {
        _readCtx = readCtx;
        _logger  = logger;
    }

    public async Task<IReadOnlyList<EvolutionaryCandidate>> ProposeOffspringAsync(
        int maxOffspring, CancellationToken ct)
    {
        if (maxOffspring <= 0) return Array.Empty<EvolutionaryCandidate>();

        var db = _readCtx.GetDbContext();

        // Parent pool: highest-Sharpe Active/Approved strategies whose generation depth
        // hasn't saturated, joined to their latest performance snapshot.
        var parentCandidates = await (
            from s in db.Set<Strategy>().AsNoTracking()
            where !s.IsDeleted
               && (s.Status == StrategyStatus.Active || s.LifecycleStage == StrategyLifecycleStage.Approved)
               && s.Generation < MaxGenerationDepth
            select new { Strategy = s }
        ).ToListAsync(ct);

        // Pull the latest snapshot per strategy for fitness.
        var fitnessByStrategy = new Dictionary<long, decimal>();
        foreach (var pc in parentCandidates)
        {
            var sharpe = await db.Set<StrategyPerformanceSnapshot>().AsNoTracking()
                .Where(p => p.StrategyId == pc.Strategy.Id && !p.IsDeleted)
                .OrderByDescending(p => p.EvaluatedAt)
                .Select(p => (decimal?)p.SharpeRatio)
                .FirstOrDefaultAsync(ct);
            fitnessByStrategy[pc.Strategy.Id] = sharpe ?? 0m;
        }

        var parents = parentCandidates
            .Where(pc => fitnessByStrategy.GetValueOrDefault(pc.Strategy.Id) >= MinParentSharpe)
            .OrderByDescending(pc => fitnessByStrategy[pc.Strategy.Id])
            .Take(ParentPoolSize)
            .Select(pc => pc.Strategy)
            .ToList();

        if (parents.Count == 0)
        {
            _logger.LogInformation(
                "EvolutionaryStrategyGenerator: no parents above MinParentSharpe={Min} — skipping cycle",
                MinParentSharpe);
            return Array.Empty<EvolutionaryCandidate>();
        }

        // Use a deterministic seed derived from the cycle minute — this keeps
        // identical inputs producing identical offspring within the minute (idempotent
        // on retry) but rotates daily so the search isn't stuck.
        int rngSeed = (int)((DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute) & int.MaxValue);
        var rng = new Random(rngSeed);

        var candidates = new List<EvolutionaryCandidate>();
        foreach (var parent in parents)
        {
            for (int i = 0; i < OffspringPerParent && candidates.Count < maxOffspring; i++)
            {
                var mutated = MutateParameters(parent.ParametersJson, rng, out string description);
                if (mutated is null) continue;

                candidates.Add(new EvolutionaryCandidate(
                    ParentStrategyId:     parent.Id,
                    Generation:           parent.Generation + 1,
                    Symbol:               parent.Symbol,
                    Timeframe:            parent.Timeframe,
                    StrategyType:         parent.StrategyType,
                    ParametersJson:       mutated,
                    MutationDescription:  description));
            }
            if (candidates.Count >= maxOffspring) break;
        }

        _logger.LogInformation(
            "EvolutionaryStrategyGenerator: proposed {Count} offspring from {ParentCount} parents (cap={Max})",
            candidates.Count, parents.Count, maxOffspring);
        return candidates;
    }

    /// <summary>
    /// Mutate every numeric parameter in <paramref name="parametersJson"/> by a
    /// uniform random factor in [1−MutationStrength, 1+MutationStrength]. Integer-
    /// typed values are rounded; non-numeric values pass through unchanged. Returns
    /// the new JSON + a human-readable description of which keys mutated.
    /// </summary>
    private static string? MutateParameters(string parametersJson, Random rng, out string description)
    {
        description = "no-op";
        if (string.IsNullOrWhiteSpace(parametersJson)) return null;

        JsonNode? root;
        try { root = JsonNode.Parse(parametersJson); }
        catch { return null; }
        if (root is not JsonObject obj) return null;

        var mutatedKeys = new List<string>();
        var keys = obj.Select(kv => kv.Key).ToList();
        foreach (var key in keys)
        {
            var value = obj[key];
            if (value is not JsonValue val) continue;
            if (!val.TryGetValue<double>(out var d)) continue;
            if (d == 0) continue; // can't multiply zero meaningfully

            double factor = 1.0 + (rng.NextDouble() * 2 - 1) * MutationStrength;
            double newValue = d * factor;

            // Preserve int-ness for parameters that look like counts (period, lookback, etc.).
            if (val.TryGetValue<int>(out _))
            {
                int rounded = (int)Math.Round(newValue);
                if (rounded == (int)d) continue;
                obj[key] = JsonValue.Create(rounded);
            }
            else
            {
                obj[key] = JsonValue.Create(Math.Round(newValue, 6));
            }
            mutatedKeys.Add(key);
        }

        if (mutatedKeys.Count == 0) return null;
        description = $"perturb({string.Join(",", mutatedKeys)}, ±{MutationStrength * 100:F0}%)";
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
