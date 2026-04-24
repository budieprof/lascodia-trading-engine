using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using CandleMarketRegime = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services.ML;

[RegisterService(ServiceLifetime.Scoped, typeof(ICpcPretrainerAuditService))]
public sealed class CpcPretrainerAuditService(
    TimeProvider? timeProvider = null,
    ILogger<CpcPretrainerAuditService>? logger = null,
    TradingMetrics? metrics = null,
    IDatabaseExceptionClassifier? dbExceptionClassifier = null)
    : ICpcPretrainerAuditService
{
    private const int AlertPayloadSchemaVersion = 2;
    private const int TrainingLogSchemaVersion = 3;
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<CpcPretrainerAuditService>? _logger = logger;
    private readonly TradingMetrics? _metrics = metrics;
    private readonly IDatabaseExceptionClassifier? _dbExceptionClassifier = dbExceptionClassifier;

    public async Task ReconcileDataQualityAlertsAsync(
        DbContext readCtx,
        DbContext writeCtx,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        var activeModelLookup = await LoadActiveModelLookupAsync(readCtx, ct);
        await ReconcileStaleEncoderAlertsAsync(readCtx, writeCtx, activeModelLookup, config, ct);
        await ReconcileConsecutiveFailureAlertsAsync(writeCtx, activeModelLookup, config, ct);
    }

    public async Task RecordStaleEncoderAlertsAsync(
        DbContext writeCtx,
        IReadOnlyList<CpcPairCandidate> candidates,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-config.StaleEncoderAlertHours);
        foreach (var candidate in candidates)
        {
            if (candidate.PriorEncoderId is null ||
                candidate.PriorTrainedAt is null ||
                candidate.PriorTrainedAt.Value > cutoff)
            {
                continue;
            }

            _metrics?.MLCpcStaleEncoders.Add(1, CpcTags(candidate, config));

            var regimeLabel = candidate.Regime?.ToString() ?? "global";
            var dedupeKey = CpcPretrainerKeys.BuildStaleEncoderAlertDedupeKey(candidate, config);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var ageHours = Math.Max(0.0, (now - candidate.PriorTrainedAt.Value).TotalHours);
            var conditionJson = JsonSerializer.Serialize(new
            {
                SchemaVersion = AlertPayloadSchemaVersion,
                Message = $"Active CPC encoder for {candidate.Symbol}/{candidate.Timeframe}/{regimeLabel}/{config.EncoderType} is stale ({ageHours:F1}h old).",
                candidate.Symbol,
                Timeframe = candidate.Timeframe.ToString(),
                Regime = regimeLabel,
                EncoderType = config.EncoderType.ToString(),
                PriorEncoderId = candidate.PriorEncoderId,
                PriorTrainedAt = candidate.PriorTrainedAt,
                AgeHours = ageHours,
                StaleEncoderAlertHours = config.StaleEncoderAlertHours,
            });

            await UpsertActiveAlertAsync(
                writeCtx,
                AlertType.DataQualityIssue,
                AlertSeverity.Medium,
                candidate.Symbol,
                dedupeKey,
                cooldownSeconds: 3600,
                conditionJson,
                now,
                ct);
        }
    }

    public async Task RaiseConfigurationDriftAlertAsync(
        DbContext writeCtx,
        string kind,
        CpcEncoderType encoderType,
        string message,
        IReadOnlyDictionary<string, object?>? extra,
        CancellationToken ct)
    {
        _metrics?.MLCpcConfigurationDriftAlerts.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("encoder_type", encoderType.ToString()));

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var dedupeKey = CpcPretrainerKeys.BuildConfigurationDriftAlertDedupeKey(kind, encoderType);
        var payload = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = AlertPayloadSchemaVersion,
            ["Kind"] = kind,
            ["EncoderType"] = encoderType.ToString(),
            ["Message"] = message,
        };
        if (extra is not null)
        {
            foreach (var kvp in extra)
                payload[kvp.Key] = kvp.Value;
        }

        await UpsertActiveAlertAsync(
            writeCtx,
            AlertType.ConfigurationDrift,
            AlertSeverity.High,
            symbol: null,
            dedupeKey,
            cooldownSeconds: 3600,
            JsonSerializer.Serialize(payload),
            now,
            ct);
    }

    public Task TryResolveConfigurationDriftAlertAsync(
        DbContext writeCtx,
        string kind,
        CpcEncoderType encoderType,
        CancellationToken ct)
        => TryResolveActiveAlertAsync(
            writeCtx,
            CpcPretrainerKeys.BuildConfigurationDriftAlertDedupeKey(kind, encoderType),
            ct);

    public async Task ResolveAllConfigurationDriftAlertsAsync(
        DbContext writeCtx,
        CancellationToken ct)
    {
        string prefix = $"{CpcPretrainerKeys.KeyPrefix}:ConfigurationDrift:";
        var dedupeKeys = await writeCtx.Set<Alert>()
            .AsNoTracking()
            .Where(a => a.IsActive
                     && !a.IsDeleted
                     && a.AlertType == AlertType.ConfigurationDrift
                     && a.DeduplicationKey != null
                     && a.DeduplicationKey.StartsWith(prefix))
            .Select(a => a.DeduplicationKey!)
            .ToListAsync(ct);

        foreach (var dedupeKey in dedupeKeys)
            await TryResolveActiveAlertAsync(writeCtx, dedupeKey, ct);
    }

    public async Task ResolveObsoleteConfigurationDriftAlertsAsync(
        DbContext writeCtx,
        CpcEncoderType activeEncoderType,
        CancellationToken ct)
    {
        string suffix = $":{activeEncoderType}";
        string prefix = $"{CpcPretrainerKeys.KeyPrefix}:ConfigurationDrift:";
        var dedupeKeys = await writeCtx.Set<Alert>()
            .AsNoTracking()
            .Where(a => a.IsActive
                     && !a.IsDeleted
                     && a.AlertType == AlertType.ConfigurationDrift
                     && a.DeduplicationKey != null
                     && a.DeduplicationKey.StartsWith(prefix)
                     && !a.DeduplicationKey.EndsWith(suffix))
            .Select(a => a.DeduplicationKey!)
            .ToListAsync(ct);

        foreach (var dedupeKey in dedupeKeys)
            await TryResolveActiveAlertAsync(writeCtx, dedupeKey, ct);
    }

    public async Task RecordRejectedAttemptAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcReason reason,
        CpcTrainingAttemptSnapshot snapshot,
        CancellationToken ct)
    {
        var strategy = writeCtx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async token =>
        {
            await using var tx = await writeCtx.Database.BeginTransactionAsync(token);

            AddTrainingLogEntity(writeCtx, candidate, config, CpcOutcome.Rejected, reason, snapshot);
            await writeCtx.SaveChangesAsync(token);

            int consecutiveFailures = await CountConsecutiveFailuresAsync(writeCtx, candidate, config, token);
            if (consecutiveFailures >= config.ConsecutiveFailAlertThreshold)
                await UpsertConsecutiveFailAlertAsync(writeCtx, candidate, config, reason, consecutiveFailures, token);

            await tx.CommitAsync(token);
        }, ct);
    }

    public Task RecordSkippedAttemptAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcReason reason,
        CpcTrainingAttemptSnapshot snapshot,
        CancellationToken ct)
        => WriteTrainingLogAsync(writeCtx, candidate, config, CpcOutcome.Skipped, reason, snapshot, ct);

    public Task RecordPromotedAttemptAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcTrainingAttemptSnapshot snapshot,
        CancellationToken ct)
        => WriteTrainingLogAsync(writeCtx, candidate, config, CpcOutcome.Promoted, CpcReason.Accepted, snapshot, ct);

    public async Task TryResolveRecoveredCandidateAlertsAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        await TryResolveActiveAlertAsync(
            writeCtx,
            CpcPretrainerKeys.BuildConsecutiveFailureAlertDedupeKey(candidate, config),
            ct);
        await TryResolveActiveAlertAsync(
            writeCtx,
            CpcPretrainerKeys.BuildStaleEncoderAlertDedupeKey(candidate, config),
            ct);
    }

    private static async Task<HashSet<(string Symbol, Timeframe Timeframe)>> LoadActiveModelLookupAsync(
        DbContext readCtx,
        CancellationToken ct)
    {
        var activePairs = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        return activePairs
            .Select(p => (p.Symbol, p.Timeframe))
            .ToHashSet();
    }

    private async Task ReconcileStaleEncoderAlertsAsync(
        DbContext readCtx,
        DbContext writeCtx,
        HashSet<(string Symbol, Timeframe Timeframe)> activeModelLookup,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        string prefix = $"{CpcPretrainerKeys.KeyPrefix}:StaleEncoder:";
        var activeAlerts = await writeCtx.Set<Alert>()
            .AsNoTracking()
            .Where(a => a.IsActive
                     && !a.IsDeleted
                     && a.AlertType == AlertType.DataQualityIssue
                     && a.DeduplicationKey != null
                     && a.DeduplicationKey.StartsWith(prefix))
            .Select(a => new { a.DeduplicationKey, a.ConditionJson })
            .ToListAsync(ct);

        if (activeAlerts.Count == 0)
            return;

        var parsedAlerts = activeAlerts
            .Select(a => TryParseTupleAlertContext(a.ConditionJson, out var context)
                ? new ActiveTupleAlert(a.DeduplicationKey!, context)
                : null)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        if (parsedAlerts.Count == 0)
            return;

        var symbols = parsedAlerts
            .Where(a => a.Context.EncoderType == config.EncoderType)
            .Select(a => a.Context.Symbol)
            .Distinct()
            .ToArray();

        var activeEncoderLookup = new Dictionary<(string Symbol, Timeframe Timeframe, CandleMarketRegime? Regime), DateTime>();
        if (symbols.Length > 0)
        {
            var activeEncoders = await readCtx.Set<MLCpcEncoder>()
                .AsNoTracking()
                .Where(e => e.IsActive
                         && !e.IsDeleted
                         && e.EncoderType == config.EncoderType
                         && symbols.Contains(e.Symbol))
                .Select(e => new { e.Id, e.Symbol, e.Timeframe, e.Regime, e.TrainedAt })
                .ToListAsync(ct);

            activeEncoderLookup = activeEncoders
                .GroupBy(e => (e.Symbol, e.Timeframe, e.Regime))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(e => e.TrainedAt)
                          .ThenByDescending(e => e.Id)
                          .First()
                          .TrainedAt);
        }

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-config.StaleEncoderAlertHours);
        foreach (var alert in parsedAlerts)
        {
            if (alert.Context.EncoderType != config.EncoderType ||
                (alert.Context.Regime is not null && !config.TrainPerRegime) ||
                !activeModelLookup.Contains((alert.Context.Symbol, alert.Context.Timeframe)))
            {
                await TryResolveActiveAlertAsync(writeCtx, alert.DeduplicationKey, ct);
                continue;
            }

            if (!activeEncoderLookup.TryGetValue(
                    (alert.Context.Symbol, alert.Context.Timeframe, alert.Context.Regime),
                    out var trainedAt) ||
                trainedAt > cutoff)
            {
                await TryResolveActiveAlertAsync(writeCtx, alert.DeduplicationKey, ct);
            }
        }
    }

    private async Task ReconcileConsecutiveFailureAlertsAsync(
        DbContext writeCtx,
        HashSet<(string Symbol, Timeframe Timeframe)> activeModelLookup,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        string prefix = $"{CpcPretrainerKeys.KeyPrefix}:";
        var activeAlerts = await writeCtx.Set<Alert>()
            .AsNoTracking()
            .Where(a => a.IsActive
                     && !a.IsDeleted
                     && a.AlertType == AlertType.DataQualityIssue
                     && a.DeduplicationKey != null
                     && a.DeduplicationKey.StartsWith(prefix)
                     && !a.DeduplicationKey.Contains(":StaleEncoder:"))
            .Select(a => new { a.DeduplicationKey, a.ConditionJson })
            .ToListAsync(ct);

        foreach (var alert in activeAlerts)
        {
            if (!TryParseTupleAlertContext(alert.ConditionJson, out var context))
                continue;

            if (context.EncoderType != config.EncoderType ||
                (context.Regime is not null && !config.TrainPerRegime) ||
                !activeModelLookup.Contains((context.Symbol, context.Timeframe)))
            {
                await TryResolveActiveAlertAsync(writeCtx, alert.DeduplicationKey!, ct);
                continue;
            }

            int consecutiveFailures = await CountConsecutiveFailuresAsync(
                writeCtx,
                new CpcPairCandidate(context.Symbol, context.Timeframe, context.Regime, null, null, null, null),
                config,
                ct);

            if (consecutiveFailures < config.ConsecutiveFailAlertThreshold)
                await TryResolveActiveAlertAsync(writeCtx, alert.DeduplicationKey!, ct);
        }
    }

    private static bool TryParseTupleAlertContext(
        string? conditionJson,
        out CpcTupleAlertContext context)
    {
        context = default!;
        if (string.IsNullOrWhiteSpace(conditionJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(conditionJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("Symbol", out var symbolElement) ||
                symbolElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("Timeframe", out var timeframeElement) ||
                timeframeElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("EncoderType", out var encoderTypeElement) ||
                encoderTypeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? symbol = symbolElement.GetString();
            string? timeframeName = timeframeElement.GetString();
            string? encoderTypeName = encoderTypeElement.GetString();
            if (string.IsNullOrWhiteSpace(symbol) ||
                !Enum.TryParse<Timeframe>(timeframeName, ignoreCase: true, out var timeframe) ||
                !Enum.TryParse<CpcEncoderType>(encoderTypeName, ignoreCase: true, out var encoderType))
            {
                return false;
            }

            CandleMarketRegime? regime = null;
            if (root.TryGetProperty("Regime", out var regimeElement) &&
                regimeElement.ValueKind == JsonValueKind.String)
            {
                string? regimeName = regimeElement.GetString();
                if (!string.IsNullOrWhiteSpace(regimeName) &&
                    !string.Equals(regimeName, "global", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Enum.TryParse<CandleMarketRegime>(regimeName, ignoreCase: true, out var parsedRegime))
                        return false;
                    regime = parsedRegime;
                }
            }

            context = new CpcTupleAlertContext(symbol, timeframe, regime, encoderType);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private Task WriteTrainingLogAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcOutcome outcome,
        CpcReason reason,
        CpcTrainingAttemptSnapshot snapshot,
        CancellationToken ct)
    {
        AddTrainingLogEntity(writeCtx, candidate, config, outcome, reason, snapshot);
        return writeCtx.SaveChangesAsync(ct);
    }

    private void AddTrainingLogEntity(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcOutcome outcome,
        CpcReason reason,
        CpcTrainingAttemptSnapshot snapshot)
    {
        var diagnostics = BuildTrainingLogDiagnostics(config, snapshot.ExtraDiagnostics);

        writeCtx.Set<MLCpcEncoderTrainingLog>().Add(new MLCpcEncoderTrainingLog
        {
            Symbol = candidate.Symbol,
            Timeframe = candidate.Timeframe,
            Regime = candidate.Regime,
            EncoderType = config.EncoderType,
            EvaluatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            Outcome = outcome.ToWire(),
            Reason = reason.ToWire(),
            PriorEncoderId = candidate.PriorEncoderId,
            PriorInfoNceLoss = candidate.PriorInfoNceLoss,
            PromotedEncoderId = snapshot.PromotedEncoderId,
            TrainInfoNceLoss = snapshot.TrainLoss,
            ValidationInfoNceLoss = snapshot.ValidationLoss,
            CandlesLoaded = snapshot.CandlesLoaded,
            CandlesAfterRegimeFilter = snapshot.CandlesAfterRegimeFilter,
            TrainingSequences = snapshot.TrainingSequences,
            ValidationSequences = snapshot.ValidationSequences,
            TrainingDurationMs = snapshot.TrainingDurationMs,
            DiagnosticsJson = JsonSerializer.Serialize(diagnostics),
        });
    }

    private static Dictionary<string, object?> BuildTrainingLogDiagnostics(
        MLCpcRuntimeConfig config,
        IReadOnlyDictionary<string, object?>? extraDiagnostics)
    {
        var diagnostics = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = TrainingLogSchemaVersion,
            ["SequenceLength"] = config.SequenceLength,
            ["SequenceStride"] = config.SequenceStride,
            ["MaxSequences"] = config.MaxSequences,
            ["ValidationSplit"] = config.ValidationSplit,
            ["MinValidationSequences"] = config.MinValidationSequences,
            ["MaxValidationLoss"] = config.MaxValidationLoss,
            ["MinValidationEmbeddingL2Norm"] = config.MinValidationEmbeddingL2Norm,
            ["MinValidationEmbeddingVariance"] = config.MinValidationEmbeddingVariance,
            ["EnableDownstreamProbeGate"] = config.EnableDownstreamProbeGate,
            ["MinDownstreamProbeSamples"] = config.MinDownstreamProbeSamples,
            ["MinDownstreamProbeBalancedAccuracy"] = config.MinDownstreamProbeBalancedAccuracy,
            ["MinDownstreamProbeImprovement"] = config.MinDownstreamProbeImprovement,
            ["EnableRepresentationDriftGate"] = config.EnableRepresentationDriftGate,
            ["MinCentroidCosineDistance"] = config.MinCentroidCosineDistance,
            ["MaxRepresentationMeanPsi"] = config.MaxRepresentationMeanPsi,
            ["EnableArchitectureSwitchGate"] = config.EnableArchitectureSwitchGate,
            ["MaxArchitectureSwitchAccuracyRegression"] = config.MaxArchitectureSwitchAccuracyRegression,
            ["EnableAdversarialValidationGate"] = config.EnableAdversarialValidationGate,
            ["MaxAdversarialValidationAuc"] = config.MaxAdversarialValidationAuc,
            ["MinAdversarialValidationSamples"] = config.MinAdversarialValidationSamples,
            ["StaleEncoderAlertHours"] = config.StaleEncoderAlertHours,
            ["PredictionSteps"] = config.PredictionSteps,
            ["EmbeddingDim"] = config.EmbeddingDim,
            ["LockTimeoutSeconds"] = config.LockTimeoutSeconds,
            ["RegimeCandleBackfillMultiplier"] = config.RegimeCandleBackfillMultiplier,
            ["ConfigurationDriftAlertCycles"] = config.ConfigurationDriftAlertCycles,
            ["SystemicPauseAlertHours"] = config.SystemicPauseAlertHours,
        };
        if (extraDiagnostics is not null)
        {
            foreach (var kvp in extraDiagnostics)
                diagnostics[kvp.Key] = kvp.Value;
        }

        return diagnostics;
    }

    private static async Task<int> CountConsecutiveFailuresAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        string promotedWire = CpcOutcome.Promoted.ToWire();
        string rejectedWire = CpcOutcome.Rejected.ToWire();

        var promotedQuery = writeCtx.Set<MLCpcEncoderTrainingLog>()
            .AsNoTracking()
            .Where(l => l.Symbol == candidate.Symbol
                     && l.Timeframe == candidate.Timeframe
                     && l.EncoderType == config.EncoderType
                     && l.Outcome == promotedWire
                     && !l.IsDeleted);
        promotedQuery = WhereTrainingLogRegime(promotedQuery, candidate.Regime);

        var lastPromotedAt = await promotedQuery
            .OrderByDescending(l => l.EvaluatedAt)
            .Select(l => (DateTime?)l.EvaluatedAt)
            .FirstOrDefaultAsync(ct);

        var rejectedQuery = writeCtx.Set<MLCpcEncoderTrainingLog>()
            .AsNoTracking()
            .Where(l => l.Symbol == candidate.Symbol
                     && l.Timeframe == candidate.Timeframe
                     && l.EncoderType == config.EncoderType
                     && l.Outcome == rejectedWire
                     && !l.IsDeleted);
        rejectedQuery = WhereTrainingLogRegime(rejectedQuery, candidate.Regime);
        if (lastPromotedAt is not null)
            rejectedQuery = rejectedQuery.Where(l => l.EvaluatedAt > lastPromotedAt.Value);

        return await rejectedQuery.CountAsync(ct);
    }

    private static IQueryable<MLCpcEncoderTrainingLog> WhereTrainingLogRegime(
        IQueryable<MLCpcEncoderTrainingLog> query,
        CandleMarketRegime? regime)
    {
        if (regime is null)
            return query.Where(l => l.Regime == null);

        var value = regime.Value;
        return query.Where(l => l.Regime == value);
    }

    private async Task UpsertConsecutiveFailAlertAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcReason reason,
        int count,
        CancellationToken ct)
    {
        var regimeLabel = candidate.Regime?.ToString() ?? "global";
        var dedupeKey = CpcPretrainerKeys.BuildConsecutiveFailureAlertDedupeKey(candidate, config);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var conditionJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = AlertPayloadSchemaVersion,
            Message = $"CPC encoder training failed {count} consecutive cycles for {candidate.Symbol}/{candidate.Timeframe}/{regimeLabel}/{config.EncoderType} (reason={reason.ToWire()}).",
            Symbol = candidate.Symbol,
            Timeframe = candidate.Timeframe.ToString(),
            Regime = regimeLabel,
            EncoderType = config.EncoderType.ToString(),
            Reason = reason.ToWire(),
            ConsecutiveFailures = count,
        });

        await UpsertActiveAlertAsync(
            writeCtx,
            AlertType.DataQualityIssue,
            AlertSeverity.Medium,
            candidate.Symbol,
            dedupeKey,
            cooldownSeconds: 3600,
            conditionJson,
            now,
            ct);
    }

    private async Task UpsertActiveAlertAsync(
        DbContext writeCtx,
        AlertType alertType,
        AlertSeverity severity,
        string? symbol,
        string dedupeKey,
        int cooldownSeconds,
        string conditionJson,
        DateTime now,
        CancellationToken ct)
    {
        if (IsPostgresProvider(writeCtx))
        {
            var outboxId = Guid.NewGuid();
            await writeCtx.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Alert"
                    ("AlertType", "Symbol", "ConditionJson", "IsActive", "LastTriggeredAt",
                     "Severity", "DeduplicationKey", "CooldownSeconds", "AutoResolvedAt", "IsDeleted", "OutboxId")
                VALUES
                    ({alertType.ToString()}, {symbol}, {conditionJson}, TRUE, {now},
                     {severity.ToString()}, {dedupeKey}, {cooldownSeconds}, NULL, FALSE, {outboxId})
                ON CONFLICT ("DeduplicationKey")
                    WHERE "IsActive" = TRUE
                      AND "IsDeleted" = FALSE
                      AND "DeduplicationKey" IS NOT NULL
                DO UPDATE SET
                    "AlertType" = EXCLUDED."AlertType",
                    "Symbol" = EXCLUDED."Symbol",
                    "ConditionJson" = EXCLUDED."ConditionJson",
                    "LastTriggeredAt" = EXCLUDED."LastTriggeredAt",
                    "Severity" = EXCLUDED."Severity",
                    "CooldownSeconds" = EXCLUDED."CooldownSeconds",
                    "AutoResolvedAt" = NULL
                """, ct);
            return;
        }

        var existing = await writeCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupeKey && a.IsActive && !a.IsDeleted)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            existing.AlertType = alertType;
            existing.Symbol = symbol;
            existing.ConditionJson = conditionJson;
            existing.LastTriggeredAt = now;
            existing.Severity = severity;
            existing.CooldownSeconds = cooldownSeconds;
            existing.AutoResolvedAt = null;
        }
        else
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType = alertType,
                Severity = severity,
                Symbol = symbol,
                DeduplicationKey = dedupeKey,
                CooldownSeconds = cooldownSeconds,
                ConditionJson = conditionJson,
                LastTriggeredAt = now,
                IsActive = true,
            });
        }

        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            LogAlertUpsertRace(dedupeKey, ex);
            writeCtx.ChangeTracker.Clear();
            await UpdateExistingActiveAlertAsync(
                writeCtx,
                alertType,
                severity,
                symbol,
                dedupeKey,
                cooldownSeconds,
                conditionJson,
                now,
                ct);
        }
    }

    private static async Task UpdateExistingActiveAlertAsync(
        DbContext writeCtx,
        AlertType alertType,
        AlertSeverity severity,
        string? symbol,
        string dedupeKey,
        int cooldownSeconds,
        string conditionJson,
        DateTime now,
        CancellationToken ct)
    {
        var existing = await writeCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupeKey && a.IsActive && !a.IsDeleted)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            throw new InvalidOperationException(
                $"Alert upsert raced for {dedupeKey}, but no active row could be reloaded.");
        }

        existing.AlertType = alertType;
        existing.Symbol = symbol;
        existing.ConditionJson = conditionJson;
        existing.LastTriggeredAt = now;
        existing.Severity = severity;
        existing.CooldownSeconds = cooldownSeconds;
        existing.AutoResolvedAt = null;
        await writeCtx.SaveChangesAsync(ct);
    }

    private async Task TryResolveActiveAlertAsync(DbContext writeCtx, string dedupeKey, CancellationToken ct)
    {
        try
        {
            await ResolveActiveAlertAsync(writeCtx, dedupeKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertResolutionFailed(dedupeKey, ex);
            writeCtx.ChangeTracker.Clear();
        }
    }

    private async Task ResolveActiveAlertAsync(DbContext writeCtx, string dedupeKey, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (IsPostgresProvider(writeCtx))
        {
            await writeCtx.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Alert"
                   SET "IsActive" = FALSE,
                       "AutoResolvedAt" = {now}
                 WHERE "DeduplicationKey" = {dedupeKey}
                   AND "IsActive" = TRUE
                   AND "IsDeleted" = FALSE
                """, ct);
            return;
        }

        var rows = await writeCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupeKey && a.IsActive && !a.IsDeleted)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return;

        foreach (var row in rows)
        {
            row.IsActive = false;
            row.AutoResolvedAt = now;
        }
        await writeCtx.SaveChangesAsync(ct);
    }

    private bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (_dbExceptionClassifier is not null)
            return _dbExceptionClassifier.IsUniqueConstraintViolation(ex);

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name == "PostgresException")
            {
                var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
                if (sqlState == "23505")
                    return true;
            }
        }

        return false;
    }

    private static bool IsPostgresProvider(DbContext ctx)
        => string.Equals(ctx.Database.ProviderName, PostgresProvider, StringComparison.Ordinal);

    private static KeyValuePair<string, object?>[] CpcTags(CpcPairCandidate candidate, MLCpcRuntimeConfig config)
        =>
        [
            new("symbol", candidate.Symbol),
            new("timeframe", candidate.Timeframe.ToString()),
            new("regime", candidate.Regime?.ToString() ?? "global"),
            new("encoder_type", config.EncoderType.ToString()),
        ];

    private void LogAlertUpsertRace(string dedupeKey, Exception ex)
        => _logger?.LogWarning(ex, "CPC alert upsert raced for {DedupeKey}; retrying against the winner.", dedupeKey);

    private void LogAlertResolutionFailed(string dedupeKey, Exception ex)
        => _logger?.LogWarning(ex, "Failed to resolve CPC alert {DedupeKey}; continuing cycle.", dedupeKey);

    private sealed record CpcTupleAlertContext(
        string Symbol,
        Timeframe Timeframe,
        CandleMarketRegime? Regime,
        CpcEncoderType EncoderType);

    private sealed record ActiveTupleAlert(
        string DeduplicationKey,
        CpcTupleAlertContext Context);
}
