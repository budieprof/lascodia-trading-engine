using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Enums;
using CandleMarketRegime = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface ICpcPretrainerCandidateSelector
{
    Task<List<CpcPairCandidate>> LoadCandidatePairsAsync(
        DbContext readCtx,
        MLCpcRuntimeConfig config,
        CancellationToken ct);
}

public interface ICpcPretrainerAuditService
{
    Task ReconcileDataQualityAlertsAsync(
        DbContext readCtx,
        DbContext writeCtx,
        MLCpcRuntimeConfig config,
        CancellationToken ct);

    Task RecordStaleEncoderAlertsAsync(
        DbContext writeCtx,
        IReadOnlyList<CpcPairCandidate> candidates,
        MLCpcRuntimeConfig config,
        CancellationToken ct);

    Task RaiseConfigurationDriftAlertAsync(
        DbContext writeCtx,
        string kind,
        CpcEncoderType encoderType,
        string message,
        IReadOnlyDictionary<string, object?>? extra,
        CancellationToken ct);

    Task TryResolveConfigurationDriftAlertAsync(
        DbContext writeCtx,
        string kind,
        CpcEncoderType encoderType,
        CancellationToken ct);

    Task ResolveAllConfigurationDriftAlertsAsync(
        DbContext writeCtx,
        CancellationToken ct);

    Task ResolveObsoleteConfigurationDriftAlertsAsync(
        DbContext writeCtx,
        CpcEncoderType activeEncoderType,
        CancellationToken ct);

    Task RecordRejectedAttemptAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcReason reason,
        CpcTrainingAttemptSnapshot snapshot,
        CancellationToken ct);

    Task RecordSkippedAttemptAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcReason reason,
        CpcTrainingAttemptSnapshot snapshot,
        CancellationToken ct);

    Task RecordPromotedAttemptAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcTrainingAttemptSnapshot snapshot,
        CancellationToken ct);

    Task TryResolveRecoveredCandidateAlertsAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CancellationToken ct);
}

public sealed record CpcPairCandidate(
    string Symbol,
    Timeframe Timeframe,
    CandleMarketRegime? Regime,
    long? PriorEncoderId,
    double? PriorInfoNceLoss,
    DateTime? PriorTrainedAt,
    long? ExpectedActiveEncoderId);

public sealed record CpcTrainingAttemptSnapshot
{
    public int CandlesLoaded { get; init; }
    public int CandlesAfterRegimeFilter { get; init; }
    public int TrainingSequences { get; init; }
    public int ValidationSequences { get; init; }
    public long TrainingDurationMs { get; init; }
    public double? TrainLoss { get; init; }
    public double? ValidationLoss { get; init; }
    public long? PromotedEncoderId { get; init; }
    public IReadOnlyDictionary<string, object?>? ExtraDiagnostics { get; init; }
}
