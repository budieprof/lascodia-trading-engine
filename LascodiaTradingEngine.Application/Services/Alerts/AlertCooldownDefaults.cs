using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.Alerts;

/// <summary>
/// Centralizes alert cooldown configuration keys and defaults.
/// Cooldown values are loaded from <see cref="EngineConfig"/> at runtime,
/// falling back to the compiled defaults if no config entry exists.
/// </summary>
public static class AlertCooldownDefaults
{
    // ── Config keys ──────────────────────────────────────────────────────────

    public const string CK_MLMonitoring           = "Alert:Cooldown:MLMonitoring";
    public const string CK_MLDrift                = "Alert:Cooldown:MLDrift";
    public const string CK_MLEscalation           = "Alert:Cooldown:MLEscalation";
    public const string CK_Optimization           = "Alert:Cooldown:Optimization";
    public const string CK_OptimizationEscalation = "Alert:Cooldown:OptimizationEscalation";
    public const string CK_Infrastructure         = "Alert:Cooldown:Infrastructure";

    // ── Defaults (seconds) ───────────────────────────────────────────────────

    public const int Default_MLMonitoring           = 3600;      // 1 hour
    public const int Default_MLDrift                = 21600;     // 6 hours
    public const int Default_MLEscalation           = 86400;     // 24 hours
    public const int Default_Optimization           = 3600;      // 1 hour
    public const int Default_OptimizationEscalation = 86400;     // 24 hours
    public const int Default_Infrastructure         = 600;       // 10 minutes

    /// <summary>
    /// Reads a cooldown value from <see cref="EngineConfig"/> by key,
    /// falling back to <paramref name="defaultSeconds"/> if the key is missing or unparseable.
    /// </summary>
    public static async Task<int> GetCooldownAsync(
        DbContext         ctx,
        string            configKey,
        int               defaultSeconds,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == configKey, ct);

        if (entry?.Value is not null && int.TryParse(entry.Value, out int seconds) && seconds > 0)
            return seconds;

        return defaultSeconds;
    }
}
