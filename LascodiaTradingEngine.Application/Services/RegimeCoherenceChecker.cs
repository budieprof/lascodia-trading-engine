using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Checks regime coherence across multiple timeframes for a symbol.
/// Returns a coherence score (0–1) indicating how aligned regimes are.
/// Low coherence (e.g., H1 says Trending but H4 says Ranging) suggests uncertainty —
/// signals generated in this state are lower quality.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class RegimeCoherenceChecker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RegimeCoherenceChecker> _logger;

    // Regimes considered "directional" (signal-friendly)
    private static readonly HashSet<MarketRegimeEnum> DirectionalRegimes = new()
    {
        MarketRegimeEnum.Trending,
        MarketRegimeEnum.Breakout
    };

    // Regimes considered "non-directional" (signal-cautious)
    private static readonly HashSet<MarketRegimeEnum> NonDirectionalRegimes = new()
    {
        MarketRegimeEnum.Ranging,
        MarketRegimeEnum.LowVolatility
    };

    public RegimeCoherenceChecker(
        IServiceScopeFactory scopeFactory,
        ILogger<RegimeCoherenceChecker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Returns a coherence score (0–1). 1.0 = all timeframes agree. 0.0 = complete disagreement.
    /// A score below 0.5 suggests the regime is uncertain and signals should be suppressed.
    /// </summary>
    public async Task<decimal> GetCoherenceScoreAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var timeframes = new[] { Timeframe.H1, Timeframe.H4, Timeframe.D1 };
        var regimes = new List<MarketRegimeEnum>();

        foreach (var tf in timeframes)
        {
            var snapshot = await readContext.GetDbContext()
                .Set<MarketRegimeSnapshot>()
                .Where(r => r.Symbol == symbol && r.Timeframe == tf && !r.IsDeleted)
                .OrderByDescending(r => r.DetectedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (snapshot is not null)
                regimes.Add(snapshot.Regime);
        }

        if (regimes.Count <= 1)
            return 1.0m; // Single or no data — no disagreement possible

        // Compute coherence: what fraction of timeframes agree with the majority?
        var majority = regimes
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .First();

        decimal coherence = (decimal)majority.Count() / regimes.Count;

        // Bonus: if all directional or all non-directional, add coherence bonus
        bool allDirectional = regimes.All(r => DirectionalRegimes.Contains(r));
        bool allNonDirectional = regimes.All(r => NonDirectionalRegimes.Contains(r));

        if (allDirectional || allNonDirectional)
            coherence = Math.Min(1.0m, coherence + 0.1m);

        return coherence;
    }
}
