using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationCheckpointCoordinator))]
internal sealed class StrategyGenerationCheckpointCoordinator : IStrategyGenerationCheckpointCoordinator
{
    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IStrategyGenerationCheckpointStore _checkpointStore;
    private readonly IStrategyCandidateSelectionPolicy _candidateSelectionPolicy;
    private readonly IStrategyParameterTemplateProvider _templateProvider;
    private readonly IStrategyGenerationHealthStore _healthStore;
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationCheckpointCoordinator(
        ILogger<StrategyGenerationWorker> logger,
        IStrategyGenerationCheckpointStore checkpointStore,
        IStrategyCandidateSelectionPolicy candidateSelectionPolicy,
        IStrategyParameterTemplateProvider templateProvider,
        IStrategyGenerationHealthStore healthStore,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _checkpointStore = checkpointStore;
        _candidateSelectionPolicy = candidateSelectionPolicy;
        _templateProvider = templateProvider;
        _healthStore = healthStore;
        _timeProvider = timeProvider;
    }

    public async Task<StrategyGenerationCheckpointResumeState> RestoreAsync(
        DbContext db,
        StrategyGenerationScreeningContext context,
        Dictionary<string, int> candidatesPerCurrency,
        Dictionary<MarketRegimeEnum, int> regimeCandidatesCreated,
        CancellationToken ct)
    {
        var completedSymbolSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int candidatesCreated = 0;
        int reserveCreated = 0;
        int candidatesScreened = 0;
        int processedSymbolsCount = 0;
        int symbolsSkipped = 0;
        var pendingCandidates = new List<ScreeningOutcome>();
        var generatedCountBySymbol = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var generatedTypeCountsBySymbol = new Dictionary<string, Dictionary<StrategyType, int>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var checkpoint = await _checkpointStore.LoadCheckpointAsync(
                db,
                _timeProvider.GetUtcNow().UtcDateTime.Date,
                ComputeFingerprint(context),
                ct);
            if (checkpoint == null)
            {
                return new StrategyGenerationCheckpointResumeState(
                    completedSymbolSet,
                    candidatesCreated,
                    reserveCreated,
                    candidatesScreened,
                    processedSymbolsCount,
                    symbolsSkipped,
                    pendingCandidates,
                    candidatesPerCurrency,
                    regimeCandidatesCreated,
                    generatedCountBySymbol,
                    generatedTypeCountsBySymbol);
            }

            completedSymbolSet = GenerationCheckpointStore.CompletedSymbolSet(checkpoint);
            candidatesCreated = checkpoint.CandidatesCreated;
            reserveCreated = checkpoint.ReserveCreated;
            candidatesScreened = checkpoint.CandidatesScreened;
            symbolsSkipped = checkpoint.SymbolsSkipped;
            processedSymbolsCount = checkpoint.SymbolsProcessed;
            pendingCandidates = checkpoint.PendingCandidates
                .Select(c => c.ToOutcome())
                .Where(c => c.Passed)
                .ToList();
            foreach (var pending in pendingCandidates)
            {
                context.ExistingSet.Add(_candidateSelectionPolicy.GetCombo(pending));
                IncrementGeneratedCounts(
                    pending.Strategy.Symbol,
                    pending.Strategy.StrategyType,
                    generatedCountBySymbol,
                    generatedTypeCountsBySymbol);
            }

            foreach (var (key, value) in checkpoint.CandidatesPerCurrency)
                candidatesPerCurrency[key] = value;
            foreach (var (key, value) in checkpoint.RegimeCandidatesCreated)
                if (Enum.TryParse<MarketRegimeEnum>(key, out var regime))
                    regimeCandidatesCreated[regime] = value;
            foreach (var (key, value) in checkpoint.CorrelationGroupCounts)
                if (int.TryParse(key, out var idx))
                    context.CorrelationGroupCounts[idx] = value;

            _logger.LogInformation(
                "StrategyGenerationWorker: resuming from checkpoint — {Completed} symbols done, {Candidates} candidates, {Pending} pending persists",
                completedSymbolSet.Count,
                candidatesCreated,
                pendingCandidates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: checkpoint restore failed — starting fresh");
            _healthStore.RecordPhaseFailure("checkpoint_restore", ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
        }

        return new StrategyGenerationCheckpointResumeState(
            completedSymbolSet,
            candidatesCreated,
            reserveCreated,
            candidatesScreened,
            processedSymbolsCount,
            symbolsSkipped,
            pendingCandidates,
            candidatesPerCurrency,
            regimeCandidatesCreated,
            generatedCountBySymbol,
            generatedTypeCountsBySymbol);
    }

    public async Task SaveAsync(
        IWriteApplicationDbContext writeCtx,
        string cycleId,
        string checkpointFingerprint,
        StrategyGenerationCheckpointProgressSnapshot snapshot,
        CancellationToken ct,
        string checkpointLabel)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var checkpointState = new GenerationCheckpointStore.State
            {
                CycleDateUtc = _timeProvider.GetUtcNow().UtcDateTime.Date,
                Fingerprint = checkpointFingerprint,
                CompletedSymbols = snapshot.CompletedSymbolSet.ToList(),
                CandidatesCreated = snapshot.CandidatesCreated,
                ReserveCreated = snapshot.ReserveCreated,
                CandidatesScreened = snapshot.CandidatesScreened,
                SymbolsProcessed = snapshot.SymbolsProcessed,
                SymbolsSkipped = snapshot.SymbolsSkipped,
                PendingCandidates = snapshot.PendingCandidates
                    .Select(GenerationCheckpointStore.PendingCandidateState.FromOutcome)
                    .ToList(),
                CandidatesPerCurrency = new Dictionary<string, int>(snapshot.CandidatesPerCurrency),
                RegimeCandidatesCreated = snapshot.RegimeCandidatesCreated.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                CorrelationGroupCounts = snapshot.CorrelationGroupCounts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        };
        var preview = GenerationCheckpointStore.SerializeWithStatus(checkpointState, _logger);
        if (preview.UsedRestartSafeFallback)
        {
            // keep metric emission local to the caller; this coordinator is about persistence only.
        }

        try
        {
            await _checkpointStore.SaveCheckpointAsync(writeCtx.GetDbContext(), cycleId, checkpointState, _logger, ct);
            await writeCtx.SaveChangesAsync(ct);
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            _healthStore.UpdateState(state => state with
            {
                LastCheckpointSavedAtUtc = nowUtc,
                LastCheckpointLabel = checkpointLabel,
                IsCheckpointPersistenceDegraded = false,
                ConsecutiveCheckpointSaveFailures = 0,
                LastCheckpointSaveFailureAtUtc = null,
                LastCheckpointSaveFailureMessage = null,
                CapturedAtUtc = nowUtc,
            });
            _healthStore.RecordPhaseSuccess(
                "checkpoint_save",
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                nowUtc);
        }
        catch (Exception ex) when (IsTransientDbError(ex))
        {
            // Single retry after 500ms for transient DB errors (timeout, deadlock, etc.)
            _logger.LogWarning(ex, "StrategyGenerationWorker: transient checkpoint save failure for {CheckpointLabel} — retrying once", checkpointLabel);
            try
            {
                await Task.Delay(500, ct);
                await _checkpointStore.SaveCheckpointAsync(writeCtx.GetDbContext(), cycleId, checkpointState, _logger, ct);
                await writeCtx.SaveChangesAsync(ct);
                var retryNowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                _healthStore.UpdateState(state => state with
                {
                    LastCheckpointSavedAtUtc = retryNowUtc,
                    LastCheckpointLabel = checkpointLabel,
                    IsCheckpointPersistenceDegraded = false,
                    ConsecutiveCheckpointSaveFailures = 0,
                    LastCheckpointSaveFailureAtUtc = null,
                    LastCheckpointSaveFailureMessage = null,
                    CapturedAtUtc = retryNowUtc,
                });
                _healthStore.RecordPhaseSuccess(
                    "checkpoint_save",
                    (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    retryNowUtc);
                _logger.LogInformation("StrategyGenerationWorker: checkpoint save succeeded on retry for {CheckpointLabel}", checkpointLabel);
                return;
            }
            catch (Exception retryEx)
            {
                _logger.LogWarning(retryEx, "StrategyGenerationWorker: checkpoint save retry also failed for {CheckpointLabel}", checkpointLabel);
            }

            RecordCheckpointSaveFailure(checkpointLabel, ex);
        }
        catch (Exception ex)
        {
            RecordCheckpointSaveFailure(checkpointLabel, ex);
        }
    }

    private void RecordCheckpointSaveFailure(string checkpointLabel, Exception ex)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        _logger.LogWarning(ex, "StrategyGenerationWorker: checkpoint save failed for {CheckpointLabel}", checkpointLabel);
        _healthStore.UpdateState(state => state with
        {
            LastCheckpointLabel = checkpointLabel,
            IsCheckpointPersistenceDegraded = true,
            ConsecutiveCheckpointSaveFailures = state.ConsecutiveCheckpointSaveFailures + 1,
            LastCheckpointSaveFailureAtUtc = nowUtc,
            LastCheckpointSaveFailureMessage = Truncate(ex.Message),
            CapturedAtUtc = nowUtc,
        });
        _healthStore.RecordPhaseFailure("checkpoint_save", ex.Message, nowUtc);
    }

    private static bool IsTransientDbError(Exception ex)
    {
        // Detect transient database errors: timeouts, deadlocks, connection resets.
        if (ex is TimeoutException) return true;
        if (ex is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException) return true;

        // Check for common SQL error patterns in the exception chain
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string message)
        => message.Length <= 500 ? message : message[..500];

    public string ComputeFingerprint(StrategyGenerationScreeningContext context)
    {
        var parts = new List<string>();

        parts.AddRange(context.RawConfigs
            .Where(kv => !StrategyGenerationCheckpointFingerprintPolicy.ExcludedConfigKeys.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"cfg|{kv.Key}|{kv.Value}"));

        parts.AddRange(context.ActivePairs
            .OrderBy(sym => sym, StringComparer.OrdinalIgnoreCase)
            .Select(sym => $"pair|{sym}"));

        parts.AddRange(context.RegimeBySymbol
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                double confidence = context.RegimeConfidenceBySymbol.GetValueOrDefault(kv.Key);
                DateTime detectedAt = context.RegimeDetectedAtBySymbol.GetValueOrDefault(kv.Key);
                return $"regime|{kv.Key}|{kv.Value}|{confidence:F6}|{detectedAt.ToUniversalTime():O}";
            }));

        parts.AddRange(context.RegimeBySymbolTf
            .OrderBy(kv => kv.Key.Item1, StringComparer.OrdinalIgnoreCase)
            .ThenBy(kv => kv.Key.Item2)
            .Select(kv => $"regime_tf|{kv.Key.Item1}|{kv.Key.Item2}|{kv.Value}"));

        parts.AddRange(context.RegimeTransitions
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"transition|{kv.Key}|{kv.Value}"));

        parts.AddRange(context.TransitionSymbols
            .OrderBy(sym => sym, StringComparer.OrdinalIgnoreCase)
            .Select(sym => $"transition_symbol|{sym}"));

        parts.AddRange(context.Existing
            .OrderBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Timeframe)
            .ThenBy(e => e.StrategyType)
            .ThenBy(e => e.Status)
            .ThenBy(e => e.LifecycleStage)
            .Select(e => $"existing|{e.Symbol}|{e.Timeframe}|{e.StrategyType}|{e.Status}|{e.LifecycleStage}"));

        parts.AddRange(context.FeedbackRates
            .OrderBy(kv => kv.Key.Item1)
            .ThenBy(kv => kv.Key.Item2)
            .ThenBy(kv => kv.Key.Item3)
            .Select(kv => $"feedback|{kv.Key.Item1}|{kv.Key.Item2}|{kv.Key.Item3}|{kv.Value:F8}"));

        parts.AddRange(context.TemplateSurvivalRates
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"template_rate|{kv.Key}|{kv.Value:F8}"));

        parts.AddRange(context.AdaptiveAdjustmentsByContext
            .OrderBy(kv => kv.Key.Item1)
            .ThenBy(kv => kv.Key.Item2)
            .Select(kv => string.Create(
                CultureInfo.InvariantCulture,
                $"adaptive|{kv.Key.Item1}|{kv.Key.Item2}|{kv.Value.WinRateMultiplier:F8}|{kv.Value.ProfitFactorMultiplier:F8}|{kv.Value.SharpeMultiplier:F8}|{kv.Value.DrawdownMultiplier:F8}")));

        if (context.Haircuts != null)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"haircuts|{context.Haircuts.WinRateHaircut:F8}|{context.Haircuts.ProfitFactorHaircut:F8}|{context.Haircuts.SharpeHaircut:F8}|{context.Haircuts.DrawdownInflation:F8}|{context.Haircuts.SampleCount}"));
        }

        if (context.PortfolioEquityCurve != null)
        {
            parts.AddRange(context.PortfolioEquityCurve
                .OrderBy(p => p.Date)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"portfolio|{p.Date:O}|{p.Equity:F8}")));
        }

        foreach (var strategyType in Enum.GetValues<StrategyType>().OrderBy(t => t))
        {
            var templates = _templateProvider.GetTemplates(strategyType) ?? [];
            for (int i = 0; i < templates.Count; i++)
                parts.Add($"template|{strategyType}|{i}|{templates[i]}");
        }

        using var sha = SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", parts));
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    private static void IncrementGeneratedCounts(
        string symbol,
        StrategyType strategyType,
        Dictionary<string, int> generatedCountBySymbol,
        Dictionary<string, Dictionary<StrategyType, int>> generatedTypeCountsBySymbol)
    {
        generatedCountBySymbol[symbol] = generatedCountBySymbol.GetValueOrDefault(symbol) + 1;

        if (!generatedTypeCountsBySymbol.TryGetValue(symbol, out var typeCounts))
        {
            typeCounts = new Dictionary<StrategyType, int>();
            generatedTypeCountsBySymbol[symbol] = typeCounts;
        }

        typeCounts[strategyType] = typeCounts.GetValueOrDefault(strategyType) + 1;
    }
}
