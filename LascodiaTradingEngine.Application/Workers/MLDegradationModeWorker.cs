using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects when all ML models for a given symbol become unavailable (no active,
/// non-suppressed models) and persists a degradation flag in <see cref="EngineConfig"/>
/// so that downstream components (e.g. <c>MLSignalScorer</c>) can skip scoring entirely
/// rather than wasting CPU on feature computation for a symbol with no usable model.
///
/// <para>
/// Escalation timeline for symbols in degradation mode:
/// <list type="bullet">
///   <item><b>First detection</b> — WARNING-level alert created, degradation flag set.</item>
///   <item><b>After 1 hour</b>   — CRITICAL-level alert created.</item>
///   <item><b>After 24 hours</b> — alert escalated to <c>ml-ops-escalation</c> destination.</item>
/// </list>
/// </para>
///
/// <para>
/// When active models return for a symbol, the degradation flag is cleared and no further
/// alerts are raised until the next degradation event.
/// </para>
///
/// <para>Configuration keys (read from <see cref="EngineConfig"/>):</para>
/// <list type="bullet">
///   <item><c>MLDegradation:PollIntervalSeconds</c>  — default 300 (5 min)</item>
///   <item><c>MLDegradation:AlertDestination</c>     — default "ml-ops"</item>
///   <item><c>MLDegradation:EscalationDestination</c> — default "ml-ops-escalation"</item>
/// </list>
///
/// <para>Per-symbol state keys managed by this worker:</para>
/// <list type="bullet">
///   <item><c>MLDegradation:{Symbol}:Active</c>     — "true" when degraded</item>
///   <item><c>MLDegradation:{Symbol}:DetectedAt</c> — ISO 8601 UTC timestamp of first detection</item>
/// </list>
/// </summary>
public sealed class MLDegradationModeWorker : BackgroundService
{
    private const string CK_PollSecs      = "MLDegradation:PollIntervalSeconds";
    private const string CK_AlertDest     = "MLDegradation:AlertDestination";
    private const string CK_EscalationDest = "MLDegradation:EscalationDestination";

    private readonly IServiceScopeFactory                _scopeFactory;
    private readonly ILogger<MLDegradationModeWorker>    _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new async DI scope per poll cycle so scoped EF Core
    /// contexts are correctly disposed after each degradation check pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLDegradationModeWorker(
        IServiceScopeFactory              scopeFactory,
        ILogger<MLDegradationModeWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Polls every <c>MLDegradation:PollIntervalSeconds</c>
    /// seconds (default 300 = 5 min), reading the interval from <see cref="EngineConfig"/>
    /// on each cycle so it can be hot-reloaded without a restart.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLDegradationModeWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 300; // default 5 min

            try
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(stoppingToken);
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var readCtx  = readDb.GetDbContext();
                    var writeCtx = writeDb.GetDbContext();

                    pollSecs = await GetConfigAsync<int>(readCtx, CK_PollSecs, 300, stoppingToken);

                    await EvaluateDegradationAsync(readCtx, writeCtx, stoppingToken);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLDegradationModeWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLDegradationModeWorker stopping.");
    }

    // ── Degradation evaluation core ──────────────────────────────────────────

    /// <summary>
    /// Groups all ML models by symbol and checks whether each symbol has at least one
    /// active, non-suppressed model. Persists or clears degradation flags and creates
    /// escalating alerts as needed.
    /// </summary>
    private async Task EvaluateDegradationAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        CancellationToken ct)
    {
        string alertDest      = await GetConfigAsync<string>(readCtx, CK_AlertDest,      "ml-ops",            ct);
        string escalationDest = await GetConfigAsync<string>(readCtx, CK_EscalationDest, "ml-ops-escalation", ct);

        // Load all distinct symbols that have ANY model record (active or not).
        var allSymbols = await readCtx.Set<MLModel>()
            .Where(m => !m.IsDeleted)
            .AsNoTracking()
            .Select(m => m.Symbol)
            .Distinct()
            .ToListAsync(ct);

        if (allSymbols.Count == 0)
        {
            _logger.LogDebug("MLDegradationModeWorker: no ML models found — skipping cycle.");
            return;
        }

        // Load the set of symbols that have at least one active, non-suppressed model.
        var healthySymbols = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsSuppressed && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => m.Symbol)
            .Distinct()
            .ToListAsync(ct);

        var healthySet = new HashSet<string>(healthySymbols, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var symbol in allSymbols)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                bool isHealthy = healthySet.Contains(symbol);

                if (isHealthy)
                {
                    await ClearDegradationAsync(symbol, writeCtx, ct);
                }
                else
                {
                    await SetDegradationAsync(symbol, alertDest, escalationDest, now, readCtx, writeCtx, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MLDegradationModeWorker: error processing symbol {Symbol} — skipping.",
                    symbol);
            }
        }
    }

    /// <summary>
    /// Sets the degradation flag for a symbol and creates escalating alerts based on
    /// how long the symbol has been degraded.
    /// </summary>
    private async Task SetDegradationAsync(
        string            symbol,
        string            alertDest,
        string            escalationDest,
        DateTime          now,
        DbContext         readCtx,
        DbContext         writeCtx,
        CancellationToken ct)
    {
        string activeKey     = $"MLDegradation:{symbol}:Active";
        string detectedAtKey = $"MLDegradation:{symbol}:DetectedAt";

        bool alreadyDegraded = await GetConfigAsync<bool>(readCtx, activeKey, false, ct);

        if (!alreadyDegraded)
        {
            // First detection: set the flag and record the detection timestamp.
            await UpsertConfigAsync(writeCtx, activeKey, "true",
                $"ML degradation flag for {symbol} — managed by MLDegradationModeWorker.", ct);
            await UpsertConfigAsync(writeCtx, detectedAtKey, now.ToString("O"),
                $"ML degradation detection timestamp for {symbol}.", ct);
            await writeCtx.SaveChangesAsync(ct);

            _logger.LogWarning(
                "MLDegradationMode: symbol {Symbol} has NO active non-suppressed models. " +
                "Degradation flag SET. Warning alert created.",
                symbol);

            // Create WARNING-level alert.
            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = symbol,
                ConditionJson = JsonSerializer.Serialize(new
                {
                    reason   = "ml_degradation_mode_activated",
                    severity = "warning",
                    symbol,
                    message  = $"All ML models for {symbol} are unavailable (inactive or suppressed). " +
                               "Signal scoring will be skipped.",
                }),
                IsActive = true,
            });

            await writeCtx.SaveChangesAsync(ct);
            return;
        }

        // Already degraded — check duration for escalation.
        string? detectedAtStr = await GetConfigAsync<string>(readCtx, detectedAtKey, string.Empty, ct);
        if (string.IsNullOrEmpty(detectedAtStr) || !DateTime.TryParse(detectedAtStr, out var detectedAt))
        {
            _logger.LogWarning(
                "MLDegradationMode: symbol {Symbol} is degraded but DetectedAt is missing or invalid. " +
                "Resetting detection timestamp.",
                symbol);
            await UpsertConfigAsync(writeCtx, detectedAtKey, now.ToString("O"),
                $"ML degradation detection timestamp for {symbol}.", ct);
            await writeCtx.SaveChangesAsync(ct);
            return;
        }

        var degradedDuration = now - detectedAt;

        // Escalation at 24 hours: escalate to ml-ops-escalation destination.
        if (degradedDuration.TotalHours >= 24)
        {
            bool escalationAlertExists = await readCtx.Set<Alert>()
                .AnyAsync(a => a.Symbol      == symbol                    &&
                               a.AlertType   == AlertType.MLModelDegraded &&
                               a.IsActive    && !a.IsDeleted, ct);

            if (!escalationAlertExists)
            {
                writeCtx.Set<Alert>().Add(new Alert
                {
                    AlertType     = AlertType.MLModelDegraded,
                    Symbol        = symbol,
                    ConditionJson = JsonSerializer.Serialize(new
                    {
                        reason          = "ml_degradation_mode_escalation",
                        severity        = "critical",
                        symbol,
                        degradedHours   = degradedDuration.TotalHours,
                        message         = $"ML degradation for {symbol} has persisted for " +
                                          $"{degradedDuration.TotalHours:F1} hours. Escalating to operations.",
                    }),
                    IsActive = true,
                });

                await writeCtx.SaveChangesAsync(ct);

                _logger.LogError(
                    "MLDegradationMode: symbol {Symbol} degraded for {Hours:F1} hours — " +
                    "ESCALATED to {Dest}.",
                    symbol, degradedDuration.TotalHours, escalationDest);
            }

            return;
        }

        // Escalation at 1 hour: create CRITICAL-level alert.
        if (degradedDuration.TotalHours >= 1)
        {
            bool criticalAlertExists = await readCtx.Set<Alert>()
                .AnyAsync(a => a.Symbol    == symbol                     &&
                               a.AlertType == AlertType.MLModelDegraded  &&
                               a.IsActive  && !a.IsDeleted               &&
                               a.ConditionJson.Contains("ml_degradation_mode_critical"), ct);

            if (!criticalAlertExists)
            {
                writeCtx.Set<Alert>().Add(new Alert
                {
                    AlertType     = AlertType.MLModelDegraded,
                    Symbol        = symbol,
                    ConditionJson = JsonSerializer.Serialize(new
                    {
                        reason         = "ml_degradation_mode_critical",
                        severity       = "critical",
                        symbol,
                        degradedHours  = degradedDuration.TotalHours,
                        message        = $"ML degradation for {symbol} has persisted for " +
                                         $"{degradedDuration.TotalHours:F1} hours. Immediate attention required.",
                    }),
                    IsActive = true,
                });

                await writeCtx.SaveChangesAsync(ct);

                _logger.LogWarning(
                    "MLDegradationMode: symbol {Symbol} degraded for {Hours:F1} hours — " +
                    "CRITICAL alert created.",
                    symbol, degradedDuration.TotalHours);
            }
        }
    }

    /// <summary>
    /// Clears the degradation flag for a symbol that now has active models.
    /// </summary>
    private async Task ClearDegradationAsync(
        string            symbol,
        DbContext         writeCtx,
        CancellationToken ct)
    {
        string activeKey     = $"MLDegradation:{symbol}:Active";
        string detectedAtKey = $"MLDegradation:{symbol}:DetectedAt";

        // Only update if the flag was previously set (avoid unnecessary writes).
        var existing = await writeCtx.Set<EngineConfig>()
            .FirstOrDefaultAsync(c => c.Key == activeKey, ct);

        if (existing is not null && existing.Value == "true")
        {
            existing.Value         = "false";
            existing.LastUpdatedAt = DateTime.UtcNow;

            // Clear the detection timestamp.
            var detectedEntry = await writeCtx.Set<EngineConfig>()
                .FirstOrDefaultAsync(c => c.Key == detectedAtKey, ct);

            if (detectedEntry is not null)
            {
                detectedEntry.Value         = string.Empty;
                detectedEntry.LastUpdatedAt = DateTime.UtcNow;
            }

            await writeCtx.SaveChangesAsync(ct);

            _logger.LogInformation(
                "MLDegradationMode: symbol {Symbol} has recovered — degradation flag CLEARED.",
                symbol);
        }
    }

    // ── Config helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table,
    /// falling back to <paramref name="defaultValue"/> if the key is absent or
    /// the stored value cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        DbContext         ctx,
        string            key,
        T                 defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    /// <summary>
    /// Creates or updates an <see cref="EngineConfig"/> entry. If the key already exists,
    /// updates its value and <see cref="EngineConfig.LastUpdatedAt"/> timestamp. Otherwise
    /// creates a new record with <see cref="ConfigDataType.String"/> and hot-reload enabled.
    /// </summary>
    private static Task UpsertConfigAsync(
        DbContext         writeCtx,
        string            key,
        string            value,
        string            description,
        CancellationToken ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(
            writeCtx, key, value, description: description, ct: ct);
}
