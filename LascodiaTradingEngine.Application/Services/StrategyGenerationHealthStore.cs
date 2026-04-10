using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationHealthStore))]
public sealed class StrategyGenerationHealthStore : IStrategyGenerationHealthStore
{
    private readonly object _gate = new();
    private StrategyGenerationHealthStateSnapshot _state = new();
    private readonly Dictionary<string, StrategyGenerationPhaseStateSnapshot> _phaseStates = new(StringComparer.Ordinal);
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<StrategyGenerationHealthStore>? _logger;
    private bool _hydrated;

    public StrategyGenerationHealthStore(
        IServiceScopeFactory? scopeFactory = null,
        ILogger<StrategyGenerationHealthStore>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void UpdateState(StrategyGenerationHealthStateSnapshot snapshot)
    {
        EnsureHydrated();
        lock (_gate)
            _state = snapshot;
    }

    public void UpdateState(Func<StrategyGenerationHealthStateSnapshot, StrategyGenerationHealthStateSnapshot> updater)
    {
        EnsureHydrated();
        lock (_gate)
            _state = updater(_state);
    }

    public void RecordPhaseSuccess(string phaseName, long durationMs, DateTime utcNow)
    {
        EnsureHydrated();
        lock (_gate)
        {
            _phaseStates.TryGetValue(phaseName, out var current);
            _phaseStates[phaseName] = (current ?? new StrategyGenerationPhaseStateSnapshot
            {
                PhaseName = phaseName
            }) with
            {
                PhaseName = phaseName,
                LastSuccessAtUtc = utcNow,
                ConsecutiveFailures = 0,
                LastDurationMs = Math.Max(0, durationMs),
            };
        }
    }

    public void RecordPhaseFailure(string phaseName, string errorMessage, DateTime utcNow)
    {
        EnsureHydrated();
        lock (_gate)
        {
            _phaseStates.TryGetValue(phaseName, out var current);
            current ??= new StrategyGenerationPhaseStateSnapshot
            {
                PhaseName = phaseName
            };
            _phaseStates[phaseName] = current with
            {
                PhaseName = phaseName,
                LastFailureAtUtc = utcNow,
                LastFailureMessage = errorMessage.Length <= 500 ? errorMessage : errorMessage[..500],
                ConsecutiveFailures = current.ConsecutiveFailures + 1,
            };
        }
    }

    public StrategyGenerationHealthStateSnapshot GetState()
    {
        EnsureHydrated();
        lock (_gate)
            return _state;
    }

    public IReadOnlyList<StrategyGenerationPhaseStateSnapshot> GetPhaseStates()
    {
        EnsureHydrated();
        lock (_gate)
        {
            return _phaseStates.Values
                .OrderBy(phase => phase.PhaseName, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private void EnsureHydrated()
    {
        if (_hydrated || _scopeFactory == null)
            return;

        lock (_gate)
        {
            if (_hydrated || _scopeFactory == null)
                return;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();

                var pendingArtifacts = db.Set<StrategyGenerationPendingArtifact>()
                    .AsNoTracking()
                    .Where(a => !a.IsDeleted)
                    .ToList();
                var activeArtifacts = pendingArtifacts
                    .Where(a => a.QuarantinedAtUtc == null)
                    .ToList();
                var latestQuarantine = pendingArtifacts
                    .Where(a => a.QuarantinedAtUtc != null)
                    .OrderByDescending(a => a.QuarantinedAtUtc)
                    .FirstOrDefault();
                var latestReplayIssue = pendingArtifacts
                    .Where(a => !string.IsNullOrWhiteSpace(a.LastErrorMessage))
                    .OrderByDescending(a => a.LastAttemptAtUtc ?? a.QuarantinedAtUtc ?? DateTime.MinValue)
                    .FirstOrDefault();

                int unresolvedFailures = db.Set<StrategyGenerationFailure>()
                    .AsNoTracking()
                    .Count(f => !f.IsDeleted && f.ResolvedAtUtc == null);

                var latestCheckpoint = db.Set<StrategyGenerationCheckpoint>()
                    .AsNoTracking()
                    .Where(c => !c.IsDeleted)
                    .OrderByDescending(c => c.LastUpdatedAtUtc)
                    .FirstOrDefault();

                var cycleRuns = db.Set<StrategyGenerationCycleRun>()
                    .AsNoTracking()
                    .Where(c => !c.IsDeleted && c.Status == "Completed")
                    .ToList();
                int pendingSummaryDispatches = cycleRuns.Count(c => c.SummaryEventId != null && c.SummaryEventDispatchedAtUtc == null);
                var latestPublishedSummary = cycleRuns
                    .Where(c => c.SummaryEventDispatchedAtUtc != null)
                    .OrderByDescending(c => c.SummaryEventDispatchedAtUtc)
                    .FirstOrDefault();
                var latestFailedSummary = cycleRuns
                    .Where(c => c.SummaryEventId != null && c.SummaryEventDispatchedAtUtc == null)
                    .OrderByDescending(c => c.SummaryEventFailedAtUtc ?? c.LastUpdatedAtUtc)
                    .FirstOrDefault();
                var latestSummaryPayload = cycleRuns
                    .Where(c => !string.IsNullOrWhiteSpace(c.SummaryEventPayloadJson))
                    .OrderByDescending(c => c.CompletedAtUtc ?? c.LastUpdatedAtUtc)
                    .Select(c => c.SummaryEventPayloadJson)
                    .FirstOrDefault();

                string? lastSkipReason = null;
                DateTime? lastSkippedAtUtc = null;
                if (!string.IsNullOrWhiteSpace(latestSummaryPayload))
                {
                    try
                    {
                        var summary = JsonSerializer.Deserialize<StrategyGenerationCycleCompletedIntegrationEvent>(latestSummaryPayload);
                        if (summary?.Skipped == true)
                        {
                            lastSkipReason = summary.SkipReason;
                            lastSkippedAtUtc = summary.CompletedAtUtc;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "StrategyGenerationHealthStore: failed to hydrate skip state from stored cycle summary payload");
                    }
                }

                _state = _state with
                {
                    PendingArtifacts = activeArtifacts.Count,
                    QuarantinedArtifacts = pendingArtifacts.Count(a => a.QuarantinedAtUtc != null),
                    OldestPendingArtifactAttemptAtUtc = activeArtifacts
                        .Where(a => a.LastAttemptAtUtc.HasValue)
                        .Select(a => a.LastAttemptAtUtc)
                        .OrderBy(a => a)
                        .FirstOrDefault(),
                    LastArtifactQuarantinedAtUtc = latestQuarantine?.QuarantinedAtUtc,
                    LastArtifactQuarantineReason = latestQuarantine?.TerminalFailureReason,
                    UnresolvedFailures = unresolvedFailures,
                    LastReplayFailureAtUtc = latestReplayIssue?.LastAttemptAtUtc ?? latestReplayIssue?.QuarantinedAtUtc,
                    LastReplayFailureMessage = latestReplayIssue?.LastErrorMessage,
                    PendingSummaryDispatches = pendingSummaryDispatches,
                    LastSkipReason = lastSkipReason,
                    LastSkippedAtUtc = lastSkippedAtUtc,
                    LastCheckpointSavedAtUtc = latestCheckpoint?.LastUpdatedAtUtc,
                    LastCheckpointLabel = latestCheckpoint?.CycleId,
                    LastSummaryPublishedAtUtc = latestPublishedSummary?.SummaryEventDispatchedAtUtc,
                    LastSummaryPublishFailureAtUtc = latestFailedSummary?.SummaryEventFailedAtUtc,
                    LastSummaryPublishFailureMessage = latestFailedSummary?.SummaryEventFailureMessage,
                    CapturedAtUtc = DateTime.UtcNow,
                };
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "StrategyGenerationHealthStore: durable state hydration failed");
            }
            finally
            {
                _hydrated = true;
            }
        }
    }
}
