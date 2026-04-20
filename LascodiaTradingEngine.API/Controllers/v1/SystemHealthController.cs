using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetDefaultsCalibrationReport;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetEngineStatus;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetStrategyGenerationWorkerHealth;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Exposes engine health status including worker states, EA connectivity, and data availability.
/// Route: api/v1/lascodia-trading-engine/health
/// </summary>
[Route("api/v1/lascodia-trading-engine/health")]
[ApiController]
public class SystemHealthController : AuthControllerBase<SystemHealthController>
{
    private readonly IWorkerHealthMonitor _workerHealthMonitor;

    public SystemHealthController(
        ILogger<SystemHealthController> logger,
        IConfiguration config,
        ICurrentUserService userService,
        IWorkerHealthMonitor workerHealthMonitor)
        : base(logger, config, userService)
    {
        _workerHealthMonitor = workerHealthMonitor;
    }

    /// <summary>Get the current engine status</summary>
    [HttpGet("status")]
    public async Task<ResponseData<EngineStatusDto>> GetStatus()
        => await Mediator.Send(new GetEngineStatusQuery());

    /// <summary>Get typed strategy-generation worker health, replay state, and phase diagnostics.</summary>
    [HttpGet("strategy-generation")]
    public async Task<ResponseData<StrategyGenerationWorkerHealthDto>> GetStrategyGenerationWorkerHealth()
        => await Mediator.Send(new GetStrategyGenerationWorkerHealthQuery());

    /// <summary>
    /// Returns real-time health snapshots for all registered background workers.
    /// Includes cycle durations, error rates, backlog depths, and staleness indicators.
    /// </summary>
    [HttpGet("workers")]
    public ActionResult<IReadOnlyList<WorkerHealthSnapshot>> GetWorkerHealth()
    {
        var snapshots = _workerHealthMonitor.GetCurrentSnapshots();
        return Ok(snapshots);
    }

    /// <summary>
    /// Builds a calibration report for the default floors introduced by the pipeline
    /// improvements (walk-forward minimums, live-vs-backtest Sharpe gate, health
    /// min-trades guard, DSR threshold, evaluation-window size). Read-only — does NOT
    /// mutate config. Review the recommendations and apply via <c>PUT /config</c>.
    /// </summary>
    /// <param name="lookbackDays">History window in days. Defaults to 180. Clamped to [30, 3650].</param>
    /// <param name="minSamples">Min sample count before a distribution-based recommendation is emitted. Clamped to [5, 1000].</param>
    [HttpGet("defaults-calibration")]
    public async Task<ResponseData<DefaultsCalibrationReportDto>> GetDefaultsCalibration(
        [FromQuery] int lookbackDays = 180,
        [FromQuery] int minSamples = 30)
        => await Mediator.Send(new GetDefaultsCalibrationReportQuery
        {
            LookbackDays = lookbackDays,
            MinSamplesForRecommendation = minSamples,
        });
}
