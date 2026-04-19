using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// One-shot migration worker that back-fills <see cref="ModelSnapshot.FeatureSchemaVersion"/>
/// on legacy ModelSnapshot JSON blobs serialised before the field existed.
///
/// <para>
/// Runtime inference works on legacy snapshots via <see cref="ModelSnapshot.ResolveFeatureSchemaVersion"/>,
/// which infers the version from the resolved feature count. That inference is correct today
/// but fragile the moment a new schema (e.g. V4) reuses a count of an older schema — without a
/// persisted tag, legacy rows would silently route to the wrong feature builder. Persisting the
/// explicit tag on every existing row locks those routing decisions to their training-time
/// semantics and removes the runtime inference dependency for all historical models.
/// </para>
///
/// <para>
/// Idempotent: gated by <c>EngineConfig["Migration:FeatureSchemaVersionBackfill:Completed"]="true"</c>
/// so subsequent startups are no-ops. Set the flag back to false (or delete the row) to force a
/// re-run — useful after a cold-restore of a stale backup.
/// </para>
/// </summary>
public sealed class FeatureSchemaVersionBackfillWorker : BackgroundService
{
    private readonly ILogger<FeatureSchemaVersionBackfillWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private const string CompletionFlagKey = "Migration:FeatureSchemaVersionBackfill:Completed";
    private const int    BatchSize          = 100;

    public FeatureSchemaVersionBackfillWorker(
        ILogger<FeatureSchemaVersionBackfillWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short initial delay so the host finishes startup migrations before we touch rows.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Clean shutdown during backfill — leave the flag unset so we retry next boot.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FeatureSchemaVersionBackfillWorker: migration failed — will retry on next startup");
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var writeCtx     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var writeDb      = writeCtx.GetDbContext();

        var flag = await writeDb.Set<EngineConfig>()
            .FirstOrDefaultAsync(c => c.Key == CompletionFlagKey && !c.IsDeleted, ct);

        if (flag is not null && string.Equals(flag.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("FeatureSchemaVersionBackfillWorker: already completed — skipping");
            return;
        }

        _logger.LogInformation("FeatureSchemaVersionBackfillWorker: starting backfill");

        long updated    = 0;
        long skipped    = 0;
        long deserFails = 0;
        long totalSeen  = 0;
        long lastId     = 0;

        while (!ct.IsCancellationRequested)
        {
            // Page by Id to avoid loading every MLModel blob into memory at once.
            var batch = await writeDb.Set<MLModel>()
                .Where(m => !m.IsDeleted && m.Id > lastId && m.ModelBytes != null)
                .OrderBy(m => m.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;
            lastId = batch[^1].Id;
            totalSeen += batch.Count;

            foreach (var model in batch)
            {
                if (ct.IsCancellationRequested) break;
                if (model.ModelBytes is null || model.ModelBytes.Length == 0) { skipped++; continue; }

                ModelSnapshot? snap;
                try
                {
                    snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes);
                }
                catch (Exception ex)
                {
                    deserFails++;
                    if (deserFails <= 5)
                        _logger.LogWarning(ex,
                            "FeatureSchemaVersionBackfillWorker: failed to deserialize snapshot for MLModel {Id} — skipping",
                            model.Id);
                    continue;
                }

                if (snap is null) { skipped++; continue; }
                if (snap.FeatureSchemaVersion > 0) { skipped++; continue; }

                snap.FeatureSchemaVersion = snap.ResolveFeatureSchemaVersion();
                if (snap.FeatureSchemaVersion <= 0) { skipped++; continue; }

                model.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
                updated++;
            }

            if (!ct.IsCancellationRequested)
                await writeCtx.SaveChangesAsync(ct);
        }

        if (ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "FeatureSchemaVersionBackfillWorker: cancelled mid-run after {Updated}/{Seen} models — will resume next startup",
                updated, totalSeen);
            return;
        }

        // Mark complete so future startups are no-ops.
        if (flag is null)
        {
            writeDb.Set<EngineConfig>().Add(new EngineConfig
            {
                Key         = CompletionFlagKey,
                Value       = "true",
                Description = $"Backfilled FeatureSchemaVersion on {updated} ModelSnapshot blob(s) at {DateTime.UtcNow:u}",
            });
        }
        else
        {
            flag.Value       = "true";
            flag.Description = $"Backfilled FeatureSchemaVersion on {updated} ModelSnapshot blob(s) at {DateTime.UtcNow:u}";
        }
        await writeCtx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "FeatureSchemaVersionBackfillWorker: complete. updated={Updated}, skipped={Skipped}, deserFails={DeserFails}, seen={Seen}",
            updated, skipped, deserFails, totalSeen);
    }
}
