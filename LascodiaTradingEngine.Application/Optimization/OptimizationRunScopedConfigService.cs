using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationRunScopedConfigService
{
    internal sealed record ConfigChange(string Key, string OldValue, string NewValue);

    private readonly OptimizationConfigProvider _configProvider;
    private readonly ILogger<OptimizationRunScopedConfigService> _logger;

    public OptimizationRunScopedConfigService(
        OptimizationConfigProvider configProvider,
        ILogger<OptimizationRunScopedConfigService> logger)
    {
        _configProvider = configProvider;
        _logger = logger;
    }

    internal async Task<OptimizationConfig> LoadPreflightConfigurationAsync(
        OptimizationRun run,
        DbContext db,
        CancellationToken ct)
    {
        bool hadExistingSnapshot = !string.IsNullOrWhiteSpace(run.ConfigSnapshotJson);
        if (TryGetRunScopedConfigSnapshot(run, out var snapshotConfig))
            return snapshotConfig;

        if (hadExistingSnapshot)
            throw new OptimizationConfigSnapshotException(run.Id);

        var liveConfig = await _configProvider.LoadAsync(db, ct);
        await _configProvider.DetectUnknownConfigKeysAsync(db, ct);
        _configProvider.LogPresetOverrides(liveConfig);
        return liveConfig;
    }

    internal async Task<OptimizationConfig> EnsureRunScopedConfigurationAsync(
        OptimizationRun run,
        OptimizationConfig config,
        DbContext db,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(run.ConfigSnapshotJson))
        {
            if (TryGetRunScopedConfigSnapshot(run, out var snapshotConfig))
                return snapshotConfig;

            throw new OptimizationConfigSnapshotException(run.Id);
        }

        var newSnapshotJson = OptimizationRunContracts.SerializeConfigSnapshot(config);

        try
        {
            var priorSnapshotJson = await db.Set<OptimizationRun>()
                .Where(r => r.Id != run.Id && r.ConfigSnapshotJson != null && !r.IsDeleted)
                .OrderByDescending(r => r.ApprovedAt ?? r.CompletedAt ?? r.ExecutionStartedAt ?? r.ClaimedAt ?? (DateTime?)r.QueuedAt ?? r.StartedAt)
                .Select(r => r.ConfigSnapshotJson)
                .FirstOrDefaultAsync(ct);

            if (priorSnapshotJson is not null)
            {
                var changes = DiffConfigSnapshots(priorSnapshotJson, newSnapshotJson);
                if (changes.Count > 0)
                {
                    _logger.LogInformation(
                        "OptimizationRunScopedConfigService: config changed since last run — {Count} parameter(s) modified: {Changes}",
                        changes.Count,
                        string.Join(", ", changes.Select(c => $"{c.Key}: {c.OldValue}→{c.NewValue}")));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationRunScopedConfigService: config diff failed (non-fatal)");
        }

        run.ConfigSnapshotJson = newSnapshotJson;
        await writeCtx.SaveChangesAsync(ct);
        return config;
    }

    internal async Task<OptimizationConfig> LoadRunScopedConfigurationAsync(
        OptimizationRun run,
        DbContext db,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct)
    {
        var config = await LoadPreflightConfigurationAsync(run, db, ct);
        return await EnsureRunScopedConfigurationAsync(run, config, db, writeCtx, ct);
    }

    internal static bool TryGetRunScopedConfigSnapshot(OptimizationRun run, out OptimizationConfig config)
        => OptimizationRunContracts.TryDeserializeConfigSnapshot(run, out config);

    internal bool TryLoadRunScopedConfigSnapshot(OptimizationRun run, out OptimizationConfig config)
        => TryGetRunScopedConfigSnapshot(run, out config);

    internal static List<ConfigChange> DiffConfigSnapshots(string priorJson, string currentJson)
    {
        var changes = new List<ConfigChange>();
        try
        {
            using var priorDoc = JsonDocument.Parse(priorJson);
            using var currentDoc = JsonDocument.Parse(currentJson);

            if (!priorDoc.RootElement.TryGetProperty("Config", out var priorConfig))
                return changes;
            if (!currentDoc.RootElement.TryGetProperty("Config", out var currentConfig))
                return changes;

            foreach (var prop in currentConfig.EnumerateObject())
            {
                if (!priorConfig.TryGetProperty(prop.Name, out var priorVal))
                {
                    changes.Add(new ConfigChange(prop.Name, "(absent)", prop.Value.ToString()));
                    continue;
                }

                bool equal = (prop.Value.ValueKind, priorVal.ValueKind) switch
                {
                    (JsonValueKind.Number, JsonValueKind.Number) =>
                        prop.Value.TryGetDouble(out double a)
                        && priorVal.TryGetDouble(out double b)
                        && a == b,
                    (JsonValueKind.True or JsonValueKind.False, JsonValueKind.True or JsonValueKind.False) =>
                        prop.Value.ValueKind == priorVal.ValueKind,
                    _ => prop.Value.ToString() == priorVal.ToString()
                };

                if (!equal)
                    changes.Add(new ConfigChange(prop.Name, priorVal.ToString(), prop.Value.ToString()));
            }
        }
        catch (JsonException)
        {
        }

        return changes;
    }
}
