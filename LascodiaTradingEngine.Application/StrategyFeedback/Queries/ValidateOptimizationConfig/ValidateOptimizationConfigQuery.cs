using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Queries.ValidateOptimizationConfig;

// ── DTO ──────────────────────────────────────────────────────────────────────

/// <summary>Result of an optimization config validation dry-run.</summary>
public class OptimizationConfigValidationDto
{
    /// <summary>True if all hard constraints pass.</summary>
    public bool IsValid { get; set; }

    /// <summary>Hard constraint violations that would cause runs to fail.</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>Suspicious-but-valid config combinations that warrant attention.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Snapshot of the resolved configuration values used for validation.</summary>
    public Dictionary<string, string> ResolvedValues { get; set; } = [];
}

// ── Query ────────────────────────────────────────────────────────────────────

/// <summary>
/// Dry-run validation of the current (or proposed) optimization configuration.
/// Loads all Optimization:* config keys, runs them through the config validator,
/// and returns errors/warnings without executing any optimization.
/// Optionally accepts override values to preview what a config change would do.
/// </summary>
public class ValidateOptimizationConfigQuery : IRequest<ResponseData<OptimizationConfigValidationDto>>
{
    /// <summary>
    /// Optional config overrides to preview. Keys should be the full config key
    /// (e.g. "Optimization:MinBootstrapCILower"). Values not provided here are
    /// loaded from the database.
    /// </summary>
    public Dictionary<string, string>? Overrides { get; set; }
}

// ── Handler ──────────────────────────────────────────────────────────────────

public class ValidateOptimizationConfigQueryHandler
    : IRequestHandler<ValidateOptimizationConfigQuery, ResponseData<OptimizationConfigValidationDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly ILogger<ValidateOptimizationConfigQueryHandler> _logger;

    public ValidateOptimizationConfigQueryHandler(
        IReadApplicationDbContext context,
        ILogger<ValidateOptimizationConfigQueryHandler> logger)
    {
        _context = context;
        _logger  = logger;
    }

    public async Task<ResponseData<OptimizationConfigValidationDto>> Handle(
        ValidateOptimizationConfigQuery request, CancellationToken ct)
    {
        var db = _context.GetDbContext();

        // Helper: load from overrides first, fall back to DB
        async Task<T> GetAsync<T>(string key, T defaultValue)
        {
            if (request.Overrides is not null && request.Overrides.TryGetValue(key, out var overrideVal))
            {
                try { return (T)Convert.ChangeType(overrideVal, typeof(T)); }
                catch { /* fall through to DB */ }
            }
            return await OptimizationGridBuilder.GetConfigAsync(db, key, defaultValue, ct);
        }

        // Load all values
        var autoApprovalImprovement = await GetAsync("Optimization:AutoApprovalImprovementThreshold", 0.10m);
        var autoApprovalMinScore    = await GetAsync("Optimization:AutoApprovalMinHealthScore", 0.55m);
        var minBootstrapCILower     = await GetAsync("Optimization:MinBootstrapCILower", 0.40m);
        var embargoRatio            = await GetAsync("Optimization:EmbargoRatio", 0.05);
        var tpeBudget               = await GetAsync("Optimization:TpeBudget", 50);
        var tpeInitialSamples       = await GetAsync("Optimization:TpeInitialSamples", 15);
        var maxParallelBacktests    = await GetAsync("Optimization:MaxParallelBacktests", 4);
        var screeningTimeoutSecs    = await GetAsync("Optimization:ScreeningTimeoutSeconds", 30);
        var correlationThreshold    = await GetAsync("Optimization:CorrelationParamThreshold", 0.15);
        var sensitivityPerturbPct   = await GetAsync("Optimization:SensitivityPerturbPct", 0.10);
        var gpEarlyStopPatience     = await GetAsync("Optimization:GpEarlyStopPatience", 4);
        var cooldownDays            = await GetAsync("Optimization:CooldownDays", 14);
        var checkpointEveryN        = await GetAsync("Optimization:CheckpointEveryN", 10);

        // Run validation
        var errors = OptimizationConfigValidator.Validate(
            autoApprovalImprovement, autoApprovalMinScore, minBootstrapCILower,
            embargoRatio, tpeBudget, tpeInitialSamples, maxParallelBacktests,
            screeningTimeoutSecs, correlationThreshold, sensitivityPerturbPct,
            gpEarlyStopPatience, cooldownDays, checkpointEveryN, _logger);

        // Collect warnings from logger (the validator logs them directly)
        // We re-run the suspicious checks here to capture them in the response
        var warnings = new List<string>();
        if (cooldownDays < 3) warnings.Add($"CooldownDays={cooldownDays} is very low — strategies may be re-optimised before results stabilise");
        if (tpeBudget < 20) warnings.Add($"TpeBudget={tpeBudget} is low — surrogate may not converge");
        if (checkpointEveryN > tpeBudget) warnings.Add($"CheckpointEveryN={checkpointEveryN} > TpeBudget={tpeBudget} — no checkpoints will fire");
        if (autoApprovalMinScore > 0.90m) warnings.Add($"AutoApprovalMinHealthScore={autoApprovalMinScore} is very high — auto-approval may rarely succeed");

        var resolved = new Dictionary<string, string>
        {
            ["Optimization:AutoApprovalImprovementThreshold"] = autoApprovalImprovement.ToString(),
            ["Optimization:AutoApprovalMinHealthScore"]       = autoApprovalMinScore.ToString(),
            ["Optimization:MinBootstrapCILower"]              = minBootstrapCILower.ToString(),
            ["Optimization:EmbargoRatio"]                     = embargoRatio.ToString(),
            ["Optimization:TpeBudget"]                        = tpeBudget.ToString(),
            ["Optimization:TpeInitialSamples"]                = tpeInitialSamples.ToString(),
            ["Optimization:MaxParallelBacktests"]              = maxParallelBacktests.ToString(),
            ["Optimization:ScreeningTimeoutSeconds"]           = screeningTimeoutSecs.ToString(),
            ["Optimization:CorrelationParamThreshold"]         = correlationThreshold.ToString(),
            ["Optimization:SensitivityPerturbPct"]             = sensitivityPerturbPct.ToString(),
            ["Optimization:GpEarlyStopPatience"]               = gpEarlyStopPatience.ToString(),
            ["Optimization:CooldownDays"]                      = cooldownDays.ToString(),
            ["Optimization:CheckpointEveryN"]                  = checkpointEveryN.ToString(),
        };

        var dto = new OptimizationConfigValidationDto
        {
            IsValid        = errors.Count == 0,
            Errors         = errors,
            Warnings       = warnings,
            ResolvedValues = resolved,
        };

        return ResponseData<OptimizationConfigValidationDto>.Init(dto);
    }
}
