using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Queries.DryRunOptimization;

// ── DTO ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Result of an optimization dry-run simulation. Shows what an optimization run
/// would look like for a given strategy without executing it.
/// </summary>
public class OptimizationDryRunDto
{
    public bool ConfigValid { get; set; }
    public List<string> ConfigIssues { get; set; } = [];
    public string PresetName { get; set; } = "";
    public int EffectiveTpeBudget { get; set; }
    public int EstimatedCandleCount { get; set; }
    public int EstimatedTrainCandles { get; set; }
    public int EstimatedTestCandles { get; set; }
    public int EstimatedEmbargoCandles { get; set; }
    public int GridSize { get; set; }
    public int FreshCandidatesAfterMemory { get; set; }
    public string SurrogateType { get; set; } = "";
    public int MaxConcurrentRuns { get; set; }
    public int CurrentlyRunning { get; set; }
    public int CurrentlyQueued { get; set; }
    public bool InBlackoutPeriod { get; set; }
    public bool InDrawdownRecovery { get; set; }
    public string? CurrentRegime { get; set; }
    public double EstimatedSpreadPoints { get; set; }
    public int CandleLookbackMonths { get; set; }
    public double EstimatedRunTimeMinutes { get; set; }
    public Dictionary<string, string> EffectiveConfig { get; set; } = [];
}

// ── Query ────────────────────────────────────────────────────────────────────

/// <summary>
/// Simulates what an optimization run would look like for a given strategy without
/// executing it. Loads config, validates, estimates candle counts, grid size, and
/// resource requirements. Lets operators tune config before committing compute.
/// </summary>
public class DryRunOptimizationQuery : IRequest<ResponseData<OptimizationDryRunDto>>
{
    public long StrategyId { get; set; }
}

// ── Handler ──────────────────────────────────────────────────────────────────

public class DryRunOptimizationQueryHandler
    : IRequestHandler<DryRunOptimizationQuery, ResponseData<OptimizationDryRunDto>>
{
    private readonly IReadApplicationDbContext _readCtx;
    private readonly OptimizationDryRunSimulator _dryRunSimulator;

    public DryRunOptimizationQueryHandler(
        IReadApplicationDbContext readCtx,
        OptimizationDryRunSimulator dryRunSimulator)
    {
        _readCtx = readCtx;
        _dryRunSimulator = dryRunSimulator;
    }

    public async Task<ResponseData<OptimizationDryRunDto>> Handle(
        DryRunOptimizationQuery request, CancellationToken ct)
    {
        var db = _readCtx.GetDbContext();
        var result = await _dryRunSimulator.SimulateAsync(db, request.StrategyId, ct);

        var dto = new OptimizationDryRunDto
        {
            ConfigValid               = result.ConfigValid,
            ConfigIssues              = result.ConfigIssues,
            PresetName                = result.PresetName,
            EffectiveTpeBudget        = result.EffectiveTpeBudget,
            EstimatedCandleCount      = result.EstimatedCandleCount,
            EstimatedTrainCandles     = result.EstimatedTrainCandles,
            EstimatedTestCandles      = result.EstimatedTestCandles,
            EstimatedEmbargoCandles   = result.EstimatedEmbargoCandles,
            GridSize                  = result.GridSize,
            FreshCandidatesAfterMemory = result.FreshCandidatesAfterMemory,
            SurrogateType             = result.SurrogateType,
            MaxConcurrentRuns         = result.MaxConcurrentRuns,
            CurrentlyRunning          = result.CurrentlyRunning,
            CurrentlyQueued           = result.CurrentlyQueued,
            InBlackoutPeriod          = result.InBlackoutPeriod,
            InDrawdownRecovery        = result.InDrawdownRecovery,
            CurrentRegime             = result.CurrentRegime,
            EstimatedSpreadPoints     = result.EstimatedSpreadPoints,
            CandleLookbackMonths      = result.CandleLookbackMonths,
            EstimatedRunTimeMinutes   = result.EstimatedRunTimeMinutes,
            EffectiveConfig           = result.EffectiveConfig,
        };

        return ResponseData<OptimizationDryRunDto>.Init(dto);
    }
}
