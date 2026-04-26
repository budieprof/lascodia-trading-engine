using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

public sealed class MLConformalChronicTripAlertUpserter : IMLConformalChronicTripAlertUpserter
{
    private const string PostgresProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // Pre-cached enum string forms used by the atomic-INSERT SQL builder; avoids
    // per-call enum-to-string allocations on the hot path.
    private static readonly string AlertTypeMLModelDegradedName = AlertType.MLModelDegraded.ToString();
    private static readonly string AlertSeverityHighName = AlertSeverity.High.ToString();

    public async Task<Alert> UpsertAsync(
        DbContext db,
        ChronicTripAlertContext context,
        CancellationToken ct)
    {
        bool isPostgres = string.Equals(db.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal);

        // Postgres: single-statement atomic upsert. Writes the latest field values
        // whether we win the insert or another replica already inserted. The post-fetch
        // tracker still needs the entity loaded so the caller's dispatcher can mutate
        // LastTriggeredAt and the worker's end-of-cycle save persists that one column.
        if (isPostgres)
        {
            string conditionJson = BuildConditionJson(context);
            string dedupKey = context.DeduplicationKey;
            string symbol = context.Symbol;
            int cooldown = context.CooldownSeconds;

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Alert""
                    (""OutboxId"", ""AlertType"", ""DeduplicationKey"", ""Symbol"", ""Severity"",
                     ""CooldownSeconds"", ""ConditionJson"", ""IsActive"", ""IsDeleted"")
                VALUES
                    (gen_random_uuid(), {AlertTypeMLModelDegradedName}, {dedupKey}, {symbol}, {AlertSeverityHighName},
                     {cooldown}, {conditionJson}, true, false)
                ON CONFLICT (""DeduplicationKey"")
                    WHERE ""IsActive"" = TRUE AND ""IsDeleted"" = FALSE AND ""DeduplicationKey"" IS NOT NULL
                DO UPDATE SET
                    ""AlertType"" = EXCLUDED.""AlertType"",
                    ""Symbol"" = EXCLUDED.""Symbol"",
                    ""Severity"" = EXCLUDED.""Severity"",
                    ""CooldownSeconds"" = EXCLUDED.""CooldownSeconds"",
                    ""ConditionJson"" = EXCLUDED.""ConditionJson"",
                    ""AutoResolvedAt"" = NULL",
                ct);
        }

        var existing = await db.Set<Alert>()
            .FirstOrDefaultAsync(a => !a.IsDeleted
                                   && a.IsActive
                                   && a.DeduplicationKey == context.DeduplicationKey, ct);

        // Non-Postgres providers (InMemoryDatabase tests, Sqlite, etc.) take the
        // read-then-add path with a tracker-mediated field update. The worker's
        // dedup-race recovery still handles the unlikely concurrent-add race for these
        // providers.
        if (existing is null)
        {
            existing = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                DeduplicationKey = context.DeduplicationKey,
                IsActive = true,
            };
            db.Set<Alert>().Add(existing);
            ApplyFields(existing, context);
        }
        else if (!isPostgres)
        {
            // Existing row found on a non-Postgres provider — the SQL upsert path didn't
            // refresh fields; do it here.
            ApplyFields(existing, context);
        }
        // Postgres path skips the field-apply: the SQL already set every relevant column,
        // and re-applying through the tracker would emit a redundant UPDATE.

        return existing;
    }

    private static string BuildConditionJson(ChronicTripAlertContext context)
        => JsonSerializer.Serialize(new
        {
            detector = "MLConformalBreaker",
            reason = "chronic_trip",
            modelId = context.ModelId,
            symbol = context.Symbol,
            timeframe = context.Timeframe.ToString(),
            consecutiveTrips = context.ConsecutiveTripStreak,
            chronicTripThreshold = context.ChronicTripThreshold,
            evaluatedAt = context.EvaluatedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });

    private static void ApplyFields(Alert alert, ChronicTripAlertContext context)
    {
        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = context.Symbol;
        alert.Severity = AlertSeverity.High;
        alert.CooldownSeconds = context.CooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = BuildConditionJson(context);
    }
}
