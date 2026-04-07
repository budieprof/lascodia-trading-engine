using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Optimization;

internal sealed record OptimizationCandidateRefinementResult(
    List<ScoredCandidate> RankedCandidates,
    int TotalIterations);

[RegisterService(ServiceLifetime.Scoped)]
internal sealed class OptimizationCandidateRefinementService
{
    private readonly OptimizationValidator _validator;
    private readonly ILogger<OptimizationCandidateRefinementService> _logger;

    public OptimizationCandidateRefinementService(
        OptimizationValidator validator,
        ILogger<OptimizationCandidateRefinementService> logger)
    {
        _validator = validator;
        _logger = logger;
    }

    internal async Task<OptimizationCandidateRefinementResult> RefineAsync(
        List<ScoredCandidate> allEvaluated,
        Strategy strategy,
        List<Candle> trainCandles,
        BacktestOptions screeningOptions,
        OptimizationConfig config,
        int totalIterations,
        CancellationToken runCt)
    {
        var evaluatedList = allEvaluated
            .OrderByDescending(c => c.HealthScore)
            .ToList();

        int keepTradesCount = Math.Max(config.TopNCandidates * 2, 10);
        for (int i = keepTradesCount; i < evaluatedList.Count; i++)
        {
            evaluatedList[i].Result.Trades?.Clear();
            evaluatedList[i].TradesTrimmed = true;
        }

        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);

        List<ScoredCandidate> topCandidates = ParetoFrontSelector.RankByNonDominatedSorting<ScoredCandidate>(
            evaluatedList,
            config.TopNCandidates,
            c => (double)c.Result.SharpeRatio,
            c => -(double)c.Result.MaxDrawdownPct,
            c => (double)c.Result.WinRate);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = config.MaxParallelBacktests,
            CancellationToken = runCt,
        };
        var fineRanked = new List<ScoredCandidate>();
        var fineLock = new object();
        await Parallel.ForEachAsync(topCandidates, parallelOptions, async (candidate, pCt) =>
        {
            try
            {
                var result = await _validator.RunWithTimeoutAsync(
                    strategy,
                    candidate.ParamsJson,
                    trainCandles,
                    screeningOptions,
                    config.ScreeningTimeoutSeconds,
                    pCt);
                Interlocked.Increment(ref totalIterations);
                lock (fineLock)
                {
                    fineRanked.Add(new ScoredCandidate(
                        candidate.ParamsJson,
                        OptimizationHealthScorer.ComputeHealthScore(result),
                        result,
                        candidate.CvCoefficientOfVariation));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OptimizationCandidateRefinementService: fine-validation backtest failed for Pareto candidate");
                Interlocked.Increment(ref totalIterations);
            }
        });

        List<ScoredCandidate> rankedCandidates = fineRanked.Count == 0
            ? topCandidates
            : fineRanked.OrderByDescending(r => r.HealthScore).ToList();

        return new OptimizationCandidateRefinementResult(rankedCandidates, totalIterations);
    }
}
