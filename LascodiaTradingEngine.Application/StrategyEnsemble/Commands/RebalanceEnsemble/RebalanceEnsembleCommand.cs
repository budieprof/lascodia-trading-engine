using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyEnsemble.Commands.RebalanceEnsemble;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Rebalances strategy ensemble weights across all active strategies based on their
/// rolling Sharpe ratios. Strategies with positive Sharpe receive proportional weight;
/// when no positive Sharpe exists, equal weight is applied.
/// </summary>
public class RebalanceEnsembleCommand : IRequest<ResponseData<string>>
{
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RebalanceEnsembleCommandValidator : AbstractValidator<RebalanceEnsembleCommand>
{
    public RebalanceEnsembleCommandValidator() { }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Loads all active strategies, fetches their latest Sharpe ratios from performance snapshots,
/// normalises to weights, and upserts <see cref="Domain.Entities.StrategyAllocation"/> records.
/// </summary>
public class RebalanceEnsembleCommandHandler
    : IRequestHandler<RebalanceEnsembleCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly IReadApplicationDbContext  _readContext;

    public RebalanceEnsembleCommandHandler(
        IWriteApplicationDbContext writeContext,
        IReadApplicationDbContext  readContext)
    {
        _writeContext = writeContext;
        _readContext  = readContext;
    }

    public async Task<ResponseData<string>> Handle(
        RebalanceEnsembleCommand request, CancellationToken cancellationToken)
    {
        // Load all active strategies
        var strategies = await _readContext.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .ToListAsync(cancellationToken);

        if (strategies.Count == 0)
            return ResponseData<string>.Init("No active strategies found", false, "No active strategies", "-14");

        // For each strategy, load the latest StrategyPerformanceSnapshot.SharpeRatio
        var sharpeMap = new Dictionary<long, decimal>();

        foreach (var strategy in strategies)
        {
            var snapshot = await _readContext.GetDbContext()
                .Set<Domain.Entities.StrategyPerformanceSnapshot>()
                .Where(s => s.StrategyId == strategy.Id && !s.IsDeleted)
                .OrderByDescending(s => s.EvaluatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            sharpeMap[strategy.Id] = snapshot?.SharpeRatio ?? 0m;
        }

        // Normalise Sharpe ratios to weights
        decimal sumPositive = sharpeMap.Values.Where(v => v > 0).Sum();

        var weights = new Dictionary<long, decimal>();

        if (sumPositive > 0)
        {
            foreach (var kvp in sharpeMap)
                weights[kvp.Key] = kvp.Value > 0 ? kvp.Value / sumPositive : 0m;
        }
        else
        {
            // Equal weight when no positive Sharpes
            decimal equalWeight = 1m / strategies.Count;
            foreach (var strategy in strategies)
                weights[strategy.Id] = equalWeight;
        }

        // Update or create StrategyAllocation for each strategy
        var writeDb = _writeContext.GetDbContext();

        foreach (var strategy in strategies)
        {
            var allocation = await writeDb.Set<Domain.Entities.StrategyAllocation>()
                .FirstOrDefaultAsync(x => x.StrategyId == strategy.Id && !x.IsDeleted, cancellationToken);

            if (allocation == null)
            {
                allocation = new Domain.Entities.StrategyAllocation
                {
                    StrategyId = strategy.Id
                };
                await writeDb.Set<Domain.Entities.StrategyAllocation>().AddAsync(allocation, cancellationToken);
            }

            allocation.Weight            = Math.Round(weights[strategy.Id], 6);
            allocation.RollingSharpRatio = sharpeMap[strategy.Id];
            allocation.LastRebalancedAt  = DateTime.UtcNow;
        }

        await _writeContext.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(
            $"Rebalanced {strategies.Count} strategies", true, "Successful", "00");
    }
}
