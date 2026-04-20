using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Operator endpoints for the global and per-strategy kill switches.
/// Route: api/v1/lascodia-trading-engine/admin/kill-switch
/// </summary>
/// <remarks>
/// <para>
/// Protected by the same <c>apiScope</c> authorization policy as every other
/// controller — production deployments should additionally restrict the
/// calling identity to an operator role at the gateway. A successful write
/// persists via <c>UpsertEngineConfigCommand</c>, which records an
/// <c>EngineConfigAuditLog</c> + <c>DecisionLog</c> entry and invalidates
/// the in-memory cache so the switch takes effect on the very next tick.
/// </para>
///
/// <para>
/// Reads deliberately go through <see cref="IKillSwitchService"/> rather than
/// directly querying <c>EngineConfig</c> so the cache-warmed hot path is
/// observable from the endpoint too — if a dashboard polls this and sees
/// stale state, the same staleness is what the signal pipeline sees.
/// </para>
/// </remarks>
[Route("api/v1/lascodia-trading-engine/admin/kill-switch")]
[ApiController]
public class KillSwitchController : AuthControllerBase<KillSwitchController>
{
    private readonly IKillSwitchService _killSwitch;

    public KillSwitchController(
        ILogger<KillSwitchController> logger,
        IConfiguration config,
        ICurrentUserService userService,
        IKillSwitchService killSwitch)
        : base(logger, config, userService)
    {
        _killSwitch = killSwitch;
    }

    // ── Global switch ────────────────────────────────────────────────────

    /// <summary>Returns the current global kill-switch state.</summary>
    [HttpGet("global")]
    public async Task<ResponseData<bool>> GetGlobalAsync(CancellationToken ct)
    {
        bool enabled = await _killSwitch.IsGlobalKilledAsync(ct);
        return ResponseData<bool>.Init(enabled, true, "Successful", "00");
    }

    /// <summary>
    /// Flips the global kill switch. When enabled, <c>StrategyWorker</c> and
    /// <c>SignalOrderBridgeWorker</c> short-circuit immediately — no new
    /// signals, no new orders. Existing positions are not touched by this
    /// switch (use position-close endpoints for flattening).
    /// </summary>
    [HttpPut("global")]
    public async Task<ResponseData<bool>> SetGlobalAsync([FromBody] KillSwitchToggleRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Reason))
            return ResponseData<bool>.Init(false, false, "Reason is required", "-11");

        await _killSwitch.SetGlobalAsync(request!.Enabled, request.Reason, ct);
        return ResponseData<bool>.Init(request.Enabled, true, "Successful", "00");
    }

    // ── Per-strategy switch ──────────────────────────────────────────────

    /// <summary>Returns the current kill-switch state for a single strategy.</summary>
    [HttpGet("strategy/{strategyId:long}")]
    public async Task<ResponseData<bool>> GetStrategyAsync(long strategyId, CancellationToken ct)
    {
        bool enabled = await _killSwitch.IsStrategyKilledAsync(strategyId, ct);
        return ResponseData<bool>.Init(enabled, true, "Successful", "00");
    }

    /// <summary>
    /// Flips the kill switch for a single strategy. Independent of the global
    /// switch — killing a single strategy does not disable others, and the
    /// global switch also kills all strategies regardless of their per-strategy
    /// state.
    /// </summary>
    [HttpPut("strategy/{strategyId:long}")]
    public async Task<ResponseData<bool>> SetStrategyAsync(long strategyId,
        [FromBody] KillSwitchToggleRequest request, CancellationToken ct)
    {
        if (strategyId <= 0)
            return ResponseData<bool>.Init(false, false, "StrategyId must be > 0", "-11");
        if (string.IsNullOrWhiteSpace(request?.Reason))
            return ResponseData<bool>.Init(false, false, "Reason is required", "-11");

        await _killSwitch.SetStrategyAsync(strategyId, request!.Enabled, request.Reason, ct);
        return ResponseData<bool>.Init(request.Enabled, true, "Successful", "00");
    }

    /// <summary>Request body for kill-switch toggles.</summary>
    public sealed class KillSwitchToggleRequest
    {
        /// <summary><c>true</c> to activate the kill switch, <c>false</c> to release.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Free-form operator-supplied justification. Written to
        /// <c>DecisionLog</c> and the logs so audits can reconstruct who flipped
        /// the switch and why.
        /// </summary>
        public string Reason { get; set; } = string.Empty;
    }
}
