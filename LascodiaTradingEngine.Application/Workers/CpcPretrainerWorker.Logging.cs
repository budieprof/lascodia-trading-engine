using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> methods for <see cref="CpcPretrainerWorker"/>.
/// All per-candidate methods assume the caller has pushed a log scope carrying
/// <c>Symbol</c>, <c>Timeframe</c>, <c>Regime</c>, and <c>EncoderType</c>, so the templates
/// do not repeat them.
/// </summary>
public sealed partial class CpcPretrainerWorker
{
    [LoggerMessage(EventId = 4300, Level = LogLevel.Information,
        Message = "CpcPretrainerWorker started.")]
    private partial void LogWorkerStarted();

    [LoggerMessage(EventId = 4309, Level = LogLevel.Error,
        Message = "CpcPretrainerWorker loop error.")]
    private partial void LogWorkerLoopError(Exception ex);

    [LoggerMessage(EventId = 4310, Level = LogLevel.Information,
        Message = "CpcPretrainerWorker stopping.")]
    private partial void LogWorkerStopping();

    [LoggerMessage(EventId = 4311, Level = LogLevel.Debug,
        Message = "CpcPretrainerWorker: disabled via config — skipping cycle.")]
    private partial void LogCycleDisabled();

    [LoggerMessage(EventId = 4312, Level = LogLevel.Debug,
        Message = "CpcPretrainerWorker: no stale pairs — nothing to train.")]
    private partial void LogNoStalePairs();

    [LoggerMessage(EventId = 4313, Level = LogLevel.Information,
        Message = "CpcPretrainerWorker cycle complete: trained={Trained} skipped={Skipped} failed={Failed} attempted={Attempted} throttled={Throttled} candidates={Total}")]
    private partial void LogCycleComplete(int trained, int skipped, int failed, int attempted, int throttled, int total);

    [LoggerMessage(EventId = 4303, Level = LogLevel.Debug,
        Message = "CpcPretrainerWorker: skipping cycle because MLTraining:SystemicPauseActive is true.")]
    private partial void LogSystemicPauseSkip();

    [LoggerMessage(EventId = 4305, Level = LogLevel.Warning,
        Message = "CpcPretrainerWorker: MLCpc:EmbeddingDim={Configured} does not match pinned CpcEmbeddingBlockSize={Pinned}. Skipping cycle.")]
    private partial void LogEmbeddingDimMismatch(int configured, int pinned);

    [LoggerMessage(EventId = 4314, Level = LogLevel.Warning,
        Message = "CpcPretrainerWorker: no ICpcPretrainer registered for EncoderType={EncoderType}. Skipping cycle.")]
    private partial void LogPretrainerMissing(CpcEncoderType encoderType);

    [LoggerMessage(EventId = 4304, Level = LogLevel.Debug,
        Message = "CpcPretrainerWorker: {Count} closed candles (<{Min}) — skipping candidate.")]
    private partial void LogInsufficientCandlesCore(int count, int min);

    private void LogInsufficientCandles(CpcPairCandidate _, int count, int min)
        => LogInsufficientCandlesCore(count, min);

    [LoggerMessage(EventId = 4315, Level = LogLevel.Debug,
        Message = "CpcPretrainerWorker: candidate produced 0 sequences — skipping.")]
    private partial void LogNoSequencesCore();

    private void LogNoSequences(CpcPairCandidate _) => LogNoSequencesCore();

    [LoggerMessage(EventId = 4316, Level = LogLevel.Information,
        Message = "CpcPretrainerWorker: candidate produced {Total} sequences but only {Validation} validation sequences (<{Min}) — skipping.")]
    private partial void LogInsufficientValidationSequencesCore(int total, int validation, int min);

    private void LogInsufficientValidationSequences(CpcPairCandidate _, int total, int validation, int min)
        => LogInsufficientValidationSequencesCore(total, validation, min);

    [LoggerMessage(EventId = 4317, Level = LogLevel.Warning,
        Message = "CpcPretrainerWorker: no ICpcPretrainer registered for EncoderType={EncoderType} — skipping candidate.")]
    private partial void LogPretrainerMissingForCandidateCore(CpcEncoderType encoderType);

    private void LogPretrainerMissingForCandidate(CpcPairCandidate _, CpcEncoderType encoderType)
        => LogPretrainerMissingForCandidateCore(encoderType);

    [LoggerMessage(EventId = 4318, Level = LogLevel.Warning,
        Message = "CpcPretrainerWorker: pretrainer threw — rejected.")]
    private partial void LogTrainerExceptionCore(Exception ex);

    private void LogTrainerException(CpcPairCandidate _, Exception ex)
        => LogTrainerExceptionCore(ex);

    [LoggerMessage(EventId = 4319, Level = LogLevel.Warning,
        Message = "CpcPretrainerWorker: pretrainer returned null — rejected.")]
    private partial void LogTrainerReturnedNullCore();

    private void LogTrainerReturnedNull(CpcPairCandidate _) => LogTrainerReturnedNullCore();

    [LoggerMessage(EventId = 4320, Level = LogLevel.Warning,
        Message = "CpcPretrainerWorker: shape gate rejected candidate — reason={Reason} trainLoss={TrainLoss} maxLoss={MaxLoss}.")]
    private partial void LogShapeGateRejectCore(string reason, double trainLoss, double maxLoss);

    private void LogShapeGateReject(CpcPairCandidate _, CpcReason reason, double trainLoss, double maxLoss)
        => LogShapeGateRejectCore(reason.ToWire(), trainLoss, maxLoss);

    [LoggerMessage(EventId = 4306, Level = LogLevel.Warning,
        Message = "CpcPretrainerWorker: projection smoke-test threw — rejected.")]
    private partial void LogProjectionInvalidThrewCore(Exception ex);

    private void LogProjectionInvalidThrew(CpcPairCandidate _, Exception ex)
        => LogProjectionInvalidThrewCore(ex);

    [LoggerMessage(EventId = 4302, Level = LogLevel.Warning,
        Message = "CpcPretrainerWorker: gate rejected candidate — reason={Reason}.")]
    private partial void LogGateRejectCore(string reason);

    private void LogGateReject(CpcPairCandidate _, string reason) => LogGateRejectCore(reason);

    [LoggerMessage(EventId = 4307, Level = LogLevel.Information,
        Message = "CpcPretrainerWorker: another worker promoted first; dropping duplicate candidate.")]
    private partial void LogPromotionConflictCore(Exception ex);

    private void LogPromotionConflict(CpcPairCandidate _, Exception ex) => LogPromotionConflictCore(ex);

    [LoggerMessage(EventId = 4301, Level = LogLevel.Information,
        Message = "CpcPretrainerWorker: promoted encoder — trainLoss={TrainLoss:F4} validationLoss={ValidationLoss:F4} trainSequences={TrainSequences} validationSequences={ValidationSequences}.")]
    private partial void LogEncoderPromotedCore(double trainLoss, double validationLoss, int trainSequences, int validationSequences);

    private void LogEncoderPromoted(CpcPairCandidate _, double trainLoss, double validationLoss, int trainSequences, int validationSequences)
        => LogEncoderPromotedCore(trainLoss, validationLoss, trainSequences, validationSequences);

    [LoggerMessage(EventId = 4321, Level = LogLevel.Information,
        Message = "CpcPretrainerWorker: another worker holds the CPC training lock for {EncoderType} — skipping candidate.")]
    private partial void LogLockBusyCore(CpcEncoderType encoderType);

    private void LogLockBusy(CpcPairCandidate _, CpcEncoderType encoderType)
        => LogLockBusyCore(encoderType);

    [LoggerMessage(EventId = 4322, Level = LogLevel.Error,
        Message = "CpcPretrainerWorker: candidate failed unexpectedly.")]
    private partial void LogUnexpectedCandidateFailureCore(Exception ex);

    private void LogUnexpectedCandidateFailure(CpcPairCandidate _, Exception ex)
        => LogUnexpectedCandidateFailureCore(ex);

    [LoggerMessage(EventId = 4323, Level = LogLevel.Error,
        Message = "CpcPretrainerWorker: failed to persist unexpected-failure audit row.")]
    private partial void LogFailureAuditPersistFailedCore(Exception ex);

    private void LogFailureAuditPersistFailed(CpcPairCandidate _, Exception ex)
        => LogFailureAuditPersistFailedCore(ex);

}
