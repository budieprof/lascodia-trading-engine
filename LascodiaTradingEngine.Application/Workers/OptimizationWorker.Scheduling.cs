using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

public partial class OptimizationWorker
{
    // ── Auto-scheduling ─────────────────────────────────────────────────────

    private async Task AutoScheduleUnderperformersAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        OptimizationConfig config,
        CancellationToken ct)
    {
        var db      = readCtx.GetDbContext();
        var writeDb = writeCtx.GetDbContext();

        // ── Gradual rollout evaluation ────────────────────────────────────
        // Check strategies with active rollouts for promotion (25→50→75→100%)
        // or rollback (performance degraded during rollout). This closes the loop
        // that StartRollout() opens during auto-approval.
        var rolloutManager = new GradualRolloutManager(_logger);
        var rollingStrategies = await writeDb.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted
                     && s.RolloutPct != null && s.RolloutPct < 100)
            .ToListAsync(ct);

        foreach (var rs in rollingStrategies)
        {
            try
            {
                int observationDays = config.CooldownDays; // Re-use cooldown as observation window
                var outcome = await rolloutManager.EvaluateRolloutAsync(
                    rs, db, config.AutoApprovalMinHealthScore, observationDays, ct);

                if (outcome is not null)
                {
                    await writeCtx.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "OptimizationWorker: rollout for strategy {Id} ({Name}) → {Outcome}",
                        rs.Id, rs.Name, outcome);

                    if (outcome == "rolledback")
                        _metrics.OptimizationAutoRejected.Add(1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OptimizationWorker: rollout evaluation failed for strategy {Id} (non-fatal)", rs.Id);
            }
        }

        // Weekly velocity cap
        try
        {
            int maxRunsPerWeek = config.MaxRunsPerWeek > 0 ? config.MaxRunsPerWeek : 20;
            var weekCutoff = DateTime.UtcNow.AddDays(-7);
            var recentRunCount = await db.Set<OptimizationRun>()
                .Where(r => !r.IsDeleted && r.StartedAt >= weekCutoff)
                .CountAsync(ct);
            if (recentRunCount >= maxRunsPerWeek)
            {
                _logger.LogInformation("OptimizationWorker: weekly velocity cap — {Count} runs in last 7 days (limit {Limit})", recentRunCount, maxRunsPerWeek);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationWorker: velocity cap check failed (non-fatal)");
        }

        var activeStrategies = await db.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .AsNoTracking()
            .Select(s => new { s.Id, s.Name, s.Symbol, s.Timeframe, s.ParametersJson })
            .ToListAsync(ct);

        if (activeStrategies.Count == 0) return;

        var pendingOptIds = await db.Set<OptimizationRun>()
            .Where(r => (r.Status == OptimizationRunStatus.Queued || r.Status == OptimizationRunStatus.Running) && !r.IsDeleted)
            .Select(r => r.StrategyId)
            .Distinct()
            .ToListAsync(ct);
        var pendingSet = new HashSet<long>(pendingOptIds);

        // Standard cooldown: strategies with a recent completed/approved run are skipped.
        // Extended cooldown: strategies with N+ consecutive auto-approval failures get
        // double the cooldown to reduce wasted compute on chronically failing strategies.
        var cooldownThreshold = DateTime.UtcNow.AddDays(-config.CooldownDays);
        var extendedCooldownThreshold = DateTime.UtcNow.AddDays(-config.CooldownDays * 2);

        var recentOptIds = await db.Set<OptimizationRun>()
            .Where(r => (r.Status == OptimizationRunStatus.Completed || r.Status == OptimizationRunStatus.Approved)
                        && !r.IsDeleted && r.CompletedAt >= cooldownThreshold)
            .Select(r => r.StrategyId)
            .Distinct()
            .ToListAsync(ct);
        var recentOptSet = new HashSet<long>(recentOptIds);

        var recentExtendedOptIds = await db.Set<OptimizationRun>()
            .Where(r => (r.Status == OptimizationRunStatus.Completed || r.Status == OptimizationRunStatus.Approved)
                        && !r.IsDeleted && r.CompletedAt >= extendedCooldownThreshold)
            .Select(r => r.StrategyId)
            .Distinct()
            .ToListAsync(ct);
        var recentExtendedOptSet = new HashSet<long>(recentExtendedOptIds);

        // Identify strategies under extended cooldown due to chronic failure.
        // Load the most recent N+1 runs per strategy to count consecutive non-approvals.
        var chronicFailureSet = new HashSet<long>();
        if (config.MaxConsecutiveFailuresBeforeEscalation > 0)
        {
            var strategiesWithRecentRuns = await db.Set<OptimizationRun>()
                .Where(r => !r.IsDeleted
                          && (r.Status == OptimizationRunStatus.Completed || r.Status == OptimizationRunStatus.Approved)
                          && r.CompletedAt >= extendedCooldownThreshold)
                .GroupBy(r => r.StrategyId)
                .Select(g => new
                {
                    StrategyId = g.Key,
                    RecentStatuses = g.OrderByDescending(r => r.CompletedAt)
                        .Take(config.MaxConsecutiveFailuresBeforeEscalation + 1)
                        .Select(r => r.Status)
                        .ToList()
                })
                .ToListAsync(ct);

            foreach (var s in strategiesWithRecentRuns)
            {
                int consecutiveFailures = 0;
                foreach (var status in s.RecentStatuses)
                {
                    if (status == OptimizationRunStatus.Approved) break;
                    consecutiveFailures++;
                }

                if (consecutiveFailures >= config.MaxConsecutiveFailuresBeforeEscalation)
                {
                    // Check if the strategy is still within the extended cooldown window.
                    // If a run completed within extendedCooldownThreshold, skip it.
                    bool withinExtendedCooldown = recentExtendedOptSet.Contains(s.StrategyId);
                    if (withinExtendedCooldown)
                        chronicFailureSet.Add(s.StrategyId);
                }
            }
        }

        var recentBacktests = await db.Set<BacktestRun>()
            .Where(r => r.Status == RunStatus.Completed && !r.IsDeleted && r.ResultJson != null)
            .GroupBy(r => r.StrategyId)
            .Select(g => new
            {
                StrategyId = g.Key,
                ResultJson = g.OrderByDescending(r => r.CompletedAt).First().ResultJson
            })
            .ToListAsync(ct);
        var backtestMap = recentBacktests.ToDictionary(r => r.StrategyId, r => r.ResultJson);

        // Batch-load the 3 most recent performance snapshots per active strategy in a
        // single query, eliminating the N+1 per-strategy query that was here previously.
        var activeStrategyIds = activeStrategies.Select(s => s.Id).ToList();
        var allRecentSnapshots = await db.Set<StrategyPerformanceSnapshot>()
            .Where(s => activeStrategyIds.Contains(s.StrategyId) && !s.IsDeleted)
            .GroupBy(s => s.StrategyId)
            .Select(g => new
            {
                StrategyId = g.Key,
                Scores = g.OrderByDescending(s => s.EvaluatedAt)
                    .Take(3)
                    .Select(s => s.HealthScore)
                    .ToList()
            })
            .ToListAsync(ct);
        var snapshotMap = allRecentSnapshots.ToDictionary(s => s.StrategyId, s => s.Scores);

        // Severity-prioritized scheduling: score all eligible strategies by urgency,
        // then schedule the worst-performing first. Strategies that fail the performance
        // gate are more urgent (priority 0) than those that pass but show deterioration
        // trends (priority 1). Within each priority tier, sort by severity of decline.
        var candidates = new List<(long StrategyId, string Name, string ParamsJson, int Priority, decimal Severity, decimal WinRate, decimal ProfitFactor)>();

        foreach (var strategy in activeStrategies)
        {
            ct.ThrowIfCancellationRequested();

            if (pendingSet.Contains(strategy.Id)) continue;
            if (recentOptSet.Contains(strategy.Id)) continue;

            if (chronicFailureSet.Contains(strategy.Id))
            {
                _logger.LogDebug(
                    "OptimizationWorker: strategy {Id} ({Name}) under extended cooldown (chronic auto-approval failure)",
                    strategy.Id, strategy.Name);
                continue;
            }

            if (!backtestMap.TryGetValue(strategy.Id, out var resultJson) || string.IsNullOrWhiteSpace(resultJson))
                continue;

            BacktestResult? result;
            try { result = JsonSerializer.Deserialize<BacktestResult>(resultJson); }
            catch { continue; }
            if (result is null) continue;

            bool meetsGate = result.TotalTrades >= config.MinTotalTrades
                && (double)result.WinRate >= config.MinWinRate
                && (double)result.ProfitFactor >= config.MinProfitFactor;

            if (meetsGate)
            {
                // Gate met — check for deterioration trend using pre-fetched snapshots
                snapshotMap.TryGetValue(strategy.Id, out var recentSnapshots);
                recentSnapshots ??= [];

                bool deteriorating = recentSnapshots.Count >= 3
                    && recentSnapshots[0] < recentSnapshots[1]
                    && recentSnapshots[1] < recentSnapshots[2];

                if (!deteriorating)
                {
                    _logger.LogDebug(
                        "OptimizationWorker: strategy {Id} ({Name}) meets performance gate — no optimization needed",
                        strategy.Id, strategy.Name);
                    continue;
                }

                // Priority 1: passes gate but deteriorating. Severity = magnitude of decline.
                decimal decline = recentSnapshots[2] - recentSnapshots[0]; // positive = how much it dropped
                candidates.Add((strategy.Id, strategy.Name, strategy.ParametersJson,
                    Priority: 1, Severity: decline, result.WinRate, result.ProfitFactor));
            }
            else
            {
                // Priority 0 (most urgent): fails performance gate outright.
                // Severity: inverse of health score — lower score = more urgent.
                decimal healthScore = OptimizationHealthScorer.ComputeHealthScore(
                    result.WinRate, result.ProfitFactor, result.MaxDrawdownPct, result.SharpeRatio, result.TotalTrades);
                candidates.Add((strategy.Id, strategy.Name, strategy.ParametersJson,
                    Priority: 0, Severity: 1m - healthScore, result.WinRate, result.ProfitFactor));
            }
        }

        // Deprioritize strategies with poor optimization ROI: if the last N runs for a
        // strategy were all non-approved (Completed but not Approved), reduce its severity
        // to push it lower in the scheduling queue.
        if (candidates.Count > 1)
        {
            var strategyIds = candidates.Select(c => c.StrategyId).ToList();
            var approvalRates = await db.Set<OptimizationRun>()
                .Where(r => strategyIds.Contains(r.StrategyId)
                         && (r.Status == OptimizationRunStatus.Completed || r.Status == OptimizationRunStatus.Approved)
                         && !r.IsDeleted)
                .GroupBy(r => r.StrategyId)
                .Select(g => new
                {
                    StrategyId = g.Key,
                    Total = g.Count(),
                    Approved = g.Count(r => r.Status == OptimizationRunStatus.Approved),
                })
                .ToListAsync(ct);
            var roiMap = approvalRates.ToDictionary(a => a.StrategyId, a => a.Total > 0 ? (double)a.Approved / a.Total : 0.5);

            candidates = candidates.Select(c =>
            {
                double roi = roiMap.GetValueOrDefault(c.StrategyId, 0.5);
                if (roi < 0.2 && roiMap.ContainsKey(c.StrategyId)) // < 20% approval rate
                {
                    _logger.LogDebug(
                        "OptimizationWorker: deprioritizing strategy {Id} ({Name}) — optimization ROI={Roi:P0}",
                        c.StrategyId, c.Name, roi);
                    return (c.StrategyId, c.Name, c.ParamsJson, Priority: c.Priority + 2, Severity: c.Severity * 0.5m, c.WinRate, c.ProfitFactor);
                }
                return c;
            }).ToList();
        }

        // Schedule worst-performing first: sort by priority (0 = gate failure first),
        // then by severity descending (largest decline / lowest score first)
        var toSchedule = candidates
            .OrderBy(c => c.Priority)
            .ThenByDescending(c => c.Severity)
            .Take(config.MaxQueuedPerCycle);

        int queued = 0;
        foreach (var candidate in toSchedule)
        {
            writeDb.Set<OptimizationRun>().Add(new OptimizationRun
            {
                StrategyId             = candidate.StrategyId,
                TriggerType            = TriggerType.Scheduled,
                Status                 = OptimizationRunStatus.Queued,
                BaselineParametersJson = CanonicalParameterJson.Normalize(candidate.ParamsJson),
                StartedAt              = DateTime.UtcNow,
            });
            queued++;

            _logger.LogInformation(
                "OptimizationWorker: auto-queued optimization for strategy {Id} ({Name}) — " +
                "priority={Prio}, severity={Sev:F2}, WR={WR:P1} PF={PF:F2}",
                candidate.StrategyId, candidate.Name, candidate.Priority,
                candidate.Severity, (double)candidate.WinRate, (double)candidate.ProfitFactor);
        }

        if (queued > 0)
            await writeCtx.SaveChangesAsync(ct);
    }

    // ── Chronic failure escalation ──────────────────────────────────────────

    /// <summary>
    /// Checks if a strategy has failed auto-approval N times consecutively. If so,
    /// creates an alert for manual attention and extends the effective cooldown by
    /// doubling it (via a temporary EngineConfig override) to reduce compute waste.
    /// </summary>
    private async Task EscalateChronicFailuresAsync(
        DbContext db, DbContext writeDb, IWriteApplicationDbContext writeCtx,
        IMediator mediator, IAlertDispatcher alertDispatcher, long strategyId,
        string strategyName, int maxConsecutiveFailures, int baseCooldownDays,
        CancellationToken ct)
    {
        // Count consecutive non-approved completed runs (most recent first)
        var recentStatuses = await db.Set<OptimizationRun>()
            .Where(r => r.StrategyId == strategyId && !r.IsDeleted
                     && (r.Status == OptimizationRunStatus.Completed || r.Status == OptimizationRunStatus.Approved))
            .OrderByDescending(r => r.CompletedAt)
            .Take(maxConsecutiveFailures + 1)
            .Select(r => r.Status)
            .ToListAsync(ct);

        int consecutiveFailures = 0;
        foreach (var status in recentStatuses)
        {
            if (status == OptimizationRunStatus.Approved) break;
            consecutiveFailures++;
        }

        if (consecutiveFailures < maxConsecutiveFailures) return;

        _logger.LogWarning(
            "OptimizationWorker: strategy {Id} ({Name}) has failed auto-approval {Count} consecutive times — escalating",
            strategyId, strategyName, consecutiveFailures);

        // Create alert for manual attention
        var alertMessage = $"Strategy '{strategyName}' (ID={strategyId}) has failed auto-approval " +
                           $"{consecutiveFailures} consecutive times. Manual parameter review recommended. " +
                           $"Cooldown extended to {baseCooldownDays * 2} days to reduce compute waste.";
        var alert = new Alert
        {
            AlertType     = AlertType.DataQualityIssue, // Closest fit for system-level alerts
            Symbol        = $"Strategy:{strategyId}",
            Channel       = AlertChannel.Webhook,
            Destination   = string.Empty, // Uses default webhook destination
            ConditionJson = JsonSerializer.Serialize(new
            {
                Type = "ChronicOptimizationFailure",
                StrategyId = strategyId,
                StrategyName = strategyName,
                ConsecutiveFailures = consecutiveFailures,
                Message = alertMessage,
            }),
            Severity        = AlertSeverity.High,
            IsActive        = true,
            LastTriggeredAt = DateTime.UtcNow,
        };
        writeDb.Set<Alert>().Add(alert);
        await writeCtx.SaveChangesAsync(ct);

        // Dispatch immediately rather than waiting for AlertWorker's next poll cycle
        try { await alertDispatcher.DispatchBySeverityAsync(alert, alertMessage, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "OptimizationWorker: immediate alert dispatch failed (non-fatal)"); }

        await mediator.Send(new LogDecisionCommand
        {
            EntityType = "Strategy", EntityId = strategyId,
            DecisionType = "ChronicOptimizationFailure",
            Outcome = $"Escalated after {consecutiveFailures} consecutive failures",
            Reason = $"Auto-approval failed {consecutiveFailures} times; alert created, cooldown extended",
            Source = "OptimizationWorker"
        }, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Timeframe? GetHigherTimeframe(Timeframe tf) => tf switch
    {
        Timeframe.M1  => Timeframe.M5,
        Timeframe.M5  => Timeframe.M15,
        Timeframe.M15 => Timeframe.H1,
        Timeframe.H1  => Timeframe.H4,
        Timeframe.H4  => Timeframe.D1,
        _             => null,
    };

    private static bool IsRegimeCompatibleWithStrategy(StrategyType strategyType, MarketRegimeEnum higherTfRegime)
    {
        if (strategyType is StrategyType.MovingAverageCrossover or StrategyType.MACDDivergence or StrategyType.MomentumTrend)
            return higherTfRegime is MarketRegimeEnum.Trending or MarketRegimeEnum.Breakout;

        if (strategyType is StrategyType.RSIReversion or StrategyType.BollingerBandReversion)
            return higherTfRegime is MarketRegimeEnum.Ranging or MarketRegimeEnum.LowVolatility;

        if (strategyType is StrategyType.BreakoutScalper or StrategyType.SessionBreakout)
            return higherTfRegime is MarketRegimeEnum.Trending or MarketRegimeEnum.HighVolatility or MarketRegimeEnum.Breakout;

        return true;
    }

    private static async Task<bool> IsInDrawdownRecoveryAsync(DbContext db, CancellationToken ct)
    {
        var latest = await db.Set<DrawdownSnapshot>()
            .Where(s => !s.IsDeleted)
            .OrderByDescending(s => s.RecordedAt)
            .FirstOrDefaultAsync(ct);

        return latest != null && latest.RecoveryMode != RecoveryMode.Normal;
    }

    /// <summary>
    /// Compares two JSON-serialised parameter sets and returns true if all numeric values
    /// are within the given relative threshold of each other.
    /// </summary>
    private static bool AreParametersSimilar(string paramsJsonA, string paramsJsonB, double threshold)
    {
        if (string.IsNullOrWhiteSpace(paramsJsonA) || string.IsNullOrWhiteSpace(paramsJsonB))
            return false;

        Dictionary<string, JsonElement>? a, b;
        try
        {
            a = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(paramsJsonA);
            b = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(paramsJsonB);
        }
        catch { return false; }

        if (a is null || b is null || a.Count == 0) return false;
        if (a.Count != b.Count) return false;
        if (!a.Keys.All(k => b.ContainsKey(k))) return false;

        int matched = 0, compared = 0;
        foreach (var (key, valA) in a)
        {
            var valB = b[key];
            if (!valA.TryGetDouble(out double dA) || !valB.TryGetDouble(out double dB))
            {
                if (valA.ToString() != valB.ToString()) return false;
                continue;
            }
            compared++;
            double denom = Math.Max(Math.Abs(dA), Math.Abs(dB));
            if (denom == 0.0) { matched++; continue; }
            if (Math.Abs(dA - dB) / denom <= threshold) matched++;
        }

        return compared > 0 && matched == compared;
    }

    /// <summary>
    /// Checks if a candidate's params are similar to any pre-parsed strategy param set.
    /// </summary>
    private static bool AreParametersSimilarToAny(
        string candidateJson,
        List<Dictionary<string, JsonElement>> otherParsed,
        double threshold)
    {
        if (string.IsNullOrWhiteSpace(candidateJson) || otherParsed.Count == 0)
            return false;

        Dictionary<string, JsonElement>? a;
        try { a = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(candidateJson); }
        catch { return false; }
        if (a is null || a.Count == 0) return false;

        foreach (var b in otherParsed)
        {
            if (a.Count != b.Count) continue;
            if (!a.Keys.All(k => b.ContainsKey(k))) continue;

            int matched = 0, compared = 0;
            bool mismatch = false;
            foreach (var (key, valA) in a)
            {
                var valB = b[key];
                if (!valA.TryGetDouble(out double dA) || !valB.TryGetDouble(out double dB))
                {
                    if (valA.ToString() != valB.ToString()) { mismatch = true; break; }
                    continue;
                }
                compared++;
                double denom = Math.Max(Math.Abs(dA), Math.Abs(dB));
                if (denom == 0.0) { matched++; continue; }
                if (Math.Abs(dA - dB) / denom <= threshold) matched++;
            }

            if (!mismatch && compared > 0 && matched == compared)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a comma-separated string of fidelity levels (e.g. "0.25,0.50") into a
    /// sorted double array.
    /// </summary>
    private double[] ParseFidelityRungs(string rungs)
    {
        double[] defaultRungs = [0.25, 0.50];
        if (string.IsNullOrWhiteSpace(rungs))
            return defaultRungs;

        var parts = rungs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<double>();
        var malformed = new List<string>();

        foreach (var part in parts)
        {
            if (double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out double val) && val > 0 && val < 1.0)
                parsed.Add(val);
            else
                malformed.Add(part);
        }

        if (malformed.Count > 0)
        {
            _logger.LogWarning(
                "OptimizationWorker: SuccessiveHalvingRungs contains malformed values ({Values}) — " +
                "these were ignored. Valid values must be between 0 and 1 exclusive",
                string.Join(", ", malformed.Select(v => $"'{v}'")));
        }

        if (parsed.Count == 0)
        {
            _logger.LogWarning(
                "OptimizationWorker: SuccessiveHalvingRungs '{Raw}' produced no valid fidelity levels — using default (0.25, 0.50)",
                rungs);
            return defaultRungs;
        }

        parsed.Sort();
        return parsed.ToArray();
    }

    private static bool IsInBlackoutPeriod(string blackoutPeriods)
    {
        if (string.IsNullOrWhiteSpace(blackoutPeriods)) return false;

        var now = DateTime.UtcNow;
        int todayOrdinal = now.Month * 100 + now.Day;

        foreach (var period in blackoutPeriods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = period.Split('-');
            if (parts.Length != 2) continue;
            if (!TryParseMonthDay(parts[0], out int startOrdinal)) continue;
            if (!TryParseMonthDay(parts[1], out int endOrdinal)) continue;

            if (startOrdinal <= endOrdinal)
            {
                if (todayOrdinal >= startOrdinal && todayOrdinal <= endOrdinal) return true;
            }
            else
            {
                if (todayOrdinal >= startOrdinal || todayOrdinal <= endOrdinal) return true;
            }
        }

        return false;

        static bool TryParseMonthDay(string s, out int ordinal)
        {
            ordinal = 0;
            var md = s.Split('/');
            if (md.Length != 2) return false;
            if (!int.TryParse(md[0], out int month) || !int.TryParse(md[1], out int day)) return false;
            if (month < 1 || month > 12 || day < 1 || day > 31) return false;
            ordinal = month * 100 + day;
            return true;
        }
    }
}
