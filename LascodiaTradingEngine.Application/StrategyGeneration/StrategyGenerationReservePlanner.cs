using LascodiaTradingEngine.Application.Common.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationReservePlanner))]
internal sealed class StrategyGenerationReservePlanner : IStrategyGenerationReservePlanner
{
    private readonly IStrategyGenerationReserveScreeningPlanner _reserveScreeningPlanner;

    public StrategyGenerationReservePlanner(IStrategyGenerationReserveScreeningPlanner reserveScreeningPlanner)
    {
        _reserveScreeningPlanner = reserveScreeningPlanner;
    }

    public Task<int> ScreenReserveCandidatesAsync(
        DbContext db,
        StrategyGenerationScreeningContext context,
        CandleLruCache candleCache,
        List<ScreeningOutcome> pendingCandidates,
        Dictionary<string, int> candidatesPerCurrency,
        int candidatesCreated,
        Dictionary<string, int> generatedCountBySymbol,
        Dictionary<string, Dictionary<LascodiaTradingEngine.Domain.Enums.StrategyType, int>> generatedTypeCountsBySymbol,
        Action onCandidateScreened,
        Func<int, Task> saveCheckpointAsync,
        CancellationToken ct)
        => _reserveScreeningPlanner.ScreenReserveCandidatesAsync(
            db,
            context,
            candleCache,
            pendingCandidates,
            candidatesPerCurrency,
            candidatesCreated,
            generatedCountBySymbol,
            generatedTypeCountsBySymbol,
            onCandidateScreened,
            saveCheckpointAsync,
            ct);
}
