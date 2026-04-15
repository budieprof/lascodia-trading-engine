using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationDynamicTemplateRefreshService))]
/// <summary>
/// Refreshes the dynamic template pool from recently promoted or approved auto strategies.
/// </summary>
internal sealed class StrategyGenerationDynamicTemplateRefreshService : IStrategyGenerationDynamicTemplateRefreshService
{
    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IStrategyParameterTemplateProvider _templateProvider;
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationDynamicTemplateRefreshService(
        ILogger<StrategyGenerationWorker> logger,
        IStrategyParameterTemplateProvider templateProvider,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _templateProvider = templateProvider;
        _timeProvider = timeProvider;
    }

    public async Task RefreshDynamicTemplatesAsync(DbContext db, CancellationToken ct)
    {
        try
        {
            // Use recently promoted strategies as the source of dynamic templates so the
            // generator can bias toward parameterizations that already survived validation.
            var promotedCutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-180);

            var qualifiedStrategies = await db.Set<Strategy>()
                .Where(s => !s.IsDeleted
                         && s.Name.StartsWith("Auto-")
                         && s.LifecycleStage >= StrategyLifecycleStage.BacktestQualified
                         && s.ParametersJson != null
                         && s.ParametersJson != "{}")
                .Select(s => new { s.Id, s.StrategyType, s.ParametersJson })
                .ToListAsync(ct);

            if (qualifiedStrategies.Count == 0)
            {
                _templateProvider.RefreshDynamicTemplates(new Dictionary<StrategyType, IReadOnlyList<string>>());
                return;
            }

            var qualifiedIds = qualifiedStrategies.Select(s => s.Id).ToList();
            var approvedOptRuns = await db.Set<OptimizationRun>()
                .Where(o => !o.IsDeleted
                         && o.Status == OptimizationRunStatus.Completed
                         && o.ApprovedAt != null
                         && o.ApprovedAt >= promotedCutoff
                         && qualifiedIds.Contains(o.StrategyId))
                .Select(o => new { o.StrategyId, o.ApprovedAt })
                .ToListAsync(ct);

            var approvedStrategyIds = new HashSet<long>(approvedOptRuns.Select(o => o.StrategyId));
            var promoted = qualifiedStrategies
                .Where(s => approvedStrategyIds.Contains(s.Id))
                .ToList();

            if (promoted.Count == 0)
            {
                _templateProvider.RefreshDynamicTemplates(new Dictionary<StrategyType, IReadOnlyList<string>>());
                return;
            }

            var approvalDateByStrategy = approvedOptRuns
                .GroupBy(o => o.StrategyId)
                .ToDictionary(g => g.Key, g => g.Max(o => o.ApprovedAt));

            var grouped = promoted
                .OrderByDescending(s => approvalDateByStrategy.GetValueOrDefault(s.Id))
                .GroupBy(x => x.StrategyType)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)g
                        .Select(x => StrategyGenerationHelpers.NormalizeTemplateParameters(x.ParametersJson!))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .ToList());

            _templateProvider.RefreshDynamicTemplates(grouped);

            int totalDynamic = grouped.Values.Sum(v => v.Count);
            if (totalDynamic > 0)
            {
                _logger.LogInformation(
                    "StrategyGenerationWorker: refreshed {Count} dynamic templates from {Types} strategy types",
                    totalDynamic,
                    grouped.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: failed to refresh dynamic templates — using static defaults");
        }
    }
}
