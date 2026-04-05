using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Provides parameter templates (as JSON strings) for each strategy type.
/// Returns static defaults merged with dynamic templates learned from promoted
/// strategies whose optimized parameters proved successful in production.
/// </summary>
public interface IStrategyParameterTemplateProvider
{
    /// <summary>
    /// Returns parameter JSON strings for the given strategy type — static defaults
    /// first, then any dynamic templates from promoted strategies.
    /// </summary>
    IReadOnlyList<string> GetTemplates(StrategyType strategyType);

    /// <summary>
    /// Refreshes the dynamic template cache from the provided promoted strategy parameters.
    /// Called once per generation cycle with data loaded from the DB.
    /// </summary>
    void RefreshDynamicTemplates(IReadOnlyDictionary<StrategyType, IReadOnlyList<string>> promotedParams);
}
