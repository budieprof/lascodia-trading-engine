using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Calibration.Queries.DTOs;
using LascodiaTradingEngine.Application.Calibration.Queries.GetCalibrationTrendReport;
using LascodiaTradingEngine.Application.Calibration.Queries.GetPagedCalibrationSnapshots;
using LascodiaTradingEngine.Application.SignalRejectionAuditNs.Queries.DTOs;
using LascodiaTradingEngine.Application.SignalRejectionAuditNs.Queries.GetPagedSignalRejections;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Operator read-only endpoints for the signal-rejection audit stream, the
/// monthly calibration snapshots, and the quarterly recalibration trend
/// report. All three routes live under the <c>/admin/</c> prefix so they're
/// easy to gate at the API gateway with operator-role authorisation.
/// Route: api/v1/lascodia-trading-engine/admin/calibration
/// </summary>
[Route("api/v1/lascodia-trading-engine/admin/calibration")]
[ApiController]
public class CalibrationAdminController : AuthControllerBase<CalibrationAdminController>
{
    public CalibrationAdminController(
        ILogger<CalibrationAdminController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>
    /// Paged list of signal rejection audit entries. Filter by stage /
    /// reason / symbol / strategy / trade-signal / time window.
    /// </summary>
    [HttpPost("signal-rejections")]
    public async Task<ResponseData<PagedData<SignalRejectionAuditDto>>> GetPagedSignalRejections(
        GetPagedSignalRejectionsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<SignalRejectionAuditDto>>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(query);
    }

    /// <summary>
    /// Paged list of monthly calibration snapshots. Filter by stage /
    /// reason / granularity / period window.
    /// </summary>
    [HttpPost("snapshots")]
    public async Task<ResponseData<PagedData<CalibrationSnapshotDto>>> GetPagedCalibrationSnapshots(
        GetPagedCalibrationSnapshotsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<CalibrationSnapshotDto>>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(query);
    }

    /// <summary>
    /// Latest-month-vs-baseline calibration trend report. The primary
    /// artifact for quarterly recalibration review: anomalies are
    /// surfaced first, followed by high-volume gates. Query params
    /// control the baseline length (default 3 months), anomaly
    /// threshold (default 15 percentage points), and minimum baseline
    /// volume (default 30).
    /// </summary>
    [HttpGet("trend-report")]
    public async Task<ResponseData<CalibrationTrendReportDto>> GetCalibrationTrendReport(
        [FromQuery] int baselineMonths = 3,
        [FromQuery] decimal anomalyThresholdPct = 0.15m,
        [FromQuery] long minBaselineCount = 30)
    {
        var query = new GetCalibrationTrendReportQuery
        {
            BaselineMonths      = baselineMonths,
            AnomalyThresholdPct = anomalyThresholdPct,
            MinBaselineCount    = minBaselineCount,
        };
        return await Mediator.Send(query);
    }
}
