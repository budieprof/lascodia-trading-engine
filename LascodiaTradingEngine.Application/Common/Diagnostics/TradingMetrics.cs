using System.Diagnostics.Metrics;

namespace LascodiaTradingEngine.Application.Common.Diagnostics;

/// <summary>
/// Centralized trading engine metrics using <see cref="System.Diagnostics.Metrics"/>.
/// Exposed via Prometheus at <c>/metrics</c>.
///
/// All instruments are created from a single <see cref="Meter"/> so they can be
/// discovered and exported together.
/// </summary>
public sealed class TradingMetrics
{
    public const string MeterName = "LascodiaTradingEngine";

    private readonly Meter _meter;

    // ── Orders ──────────────────────────────────────────────────────────────
    public Counter<long>     OrdersSubmitted        { get; }
    public Counter<long>     OrdersFilled           { get; }
    public Counter<long>     OrdersFailed           { get; }
    public Histogram<double> OrderFillLatencyMs     { get; }
    public Histogram<double> OrderSlippagePips      { get; }

    // ── Signals ─────────────────────────────────────────────────────────────
    public Counter<long>     SignalsGenerated       { get; }
    public Counter<long>     SignalsAccepted        { get; }
    public Counter<long>     SignalsRejected        { get; }
    public Counter<long>     SignalsFiltered        { get; }
    public Counter<long>     EvaluatorRejections    { get; }
    public Counter<long>     SignalsSuppressed      { get; }
    public Counter<long>     SignalCooldownSkips    { get; }
    public Counter<long>     TicksDroppedLockBusy   { get; }
    public Counter<long>     TicksDroppedBackpressure { get; }
    public Counter<long>     TicksSkippedNoEA       { get; }
    public Counter<long>     TicksSkippedHealthCritical { get; }
    public Counter<long>     TicksSkippedStale      { get; }
    public Counter<long>     TicksSkippedNoLivePrice { get; }
    public Counter<long>     StrategiesCircuitBroken { get; }
    public Histogram<double> StrategyEvaluationMs   { get; }
    public Histogram<double> SignalDedupLatencyMs   { get; }
    public Counter<long>     SignalDedupDuplicates  { get; }
    public Counter<long>     EvaluatorMissing       { get; }
    public Counter<long>     PrefetchQueryTimeouts  { get; }
    public Counter<long>     StrategyMetricsCacheHits { get; }
    public Counter<long>     StrategyMetricsCacheMisses { get; }
    public Counter<long>     StrategyMetricsCacheInvalidations { get; }
    public Counter<long>     SignalTtlExtendedMarketClosed { get; }
    public Counter<long>     MLModelStaleRejections { get; }
    public Counter<long>     MLScoringTimeouts      { get; }
    public Counter<long>     Tier2PriceDriftRejections { get; }
    public Counter<long>     SignalExpirySkewToleranceApplied { get; }
    public Counter<long>     MarketRegimeCacheHits  { get; }
    public Counter<long>     MarketRegimeCacheMisses { get; }
    public Counter<long>     EngineConfigCacheHits  { get; }
    public Counter<long>     EngineConfigCacheMisses { get; }
    public Counter<long>     EngineConfigExpiredEntries { get; }
    public Counter<long>     EngineConfigStaleMetricsBlocksPruned { get; }
    public Counter<long>     EngineConfigStaleMetricsEntriesPruned { get; }
    public Histogram<double> EngineConfigExpiryCycleDurationMs { get; }
    public Counter<long>     KillSwitchTriggered    { get; }
    public Counter<long>     CircuitBreakerTransitions { get; }
    public Counter<long>     CircuitBreakerShortCircuits { get; }
    public Counter<long>     DbBulkheadWaits        { get; }
    public Histogram<double> DbBulkheadWaitMs       { get; }
    public Counter<long>     MLScoringBatchCalls    { get; }
    public Histogram<double> MLScoringBatchSize     { get; }
    public Counter<long>     ConflictResolutionEarlyExits { get; }
    public Counter<long>     EaReconciliationDrift  { get; }
    public Histogram<double> EaReconciliationMeanDriftPerRun { get; }
    public Histogram<double> EaReconciliationWindowRunCount { get; }
    public Counter<long>     EaReconciliationAlertTransitions { get; }
    public Counter<long>     RetentionRowsDeleted   { get; }
    public Histogram<double> StrategyLockAcquisitionMs { get; }
    public Counter<long>     RegimeParamsCacheHits  { get; }
    public Counter<long>     RegimeParamsCacheMisses { get; }
    public Counter<long>     SignalRejectionsAudited { get; }
    public Counter<long>     CalibrationSnapshotsWritten { get; }

    // ── Validation / promotion defaults telemetry ────────────────────────────
    // Tagged counters that let operators calibrate the new default floors added in
    // WalkForwardWorker (MinInSampleDays, MinOutOfSampleDays, MinCandlesPerFold,
    // MinTradesPerFold) and StrategyPromotionWorker (MinLiveVsBacktestSharpeRatio).
    // If these fire a lot, the floors are probably too aggressive.
    public Counter<long>     WalkForwardFoldRejections     { get; }
    public Counter<long>     PromotionGateRejections       { get; }

    // ── Positions ───────────────────────────────────────────────────────────
    public Counter<long>     PositionsOpened        { get; }
    public Counter<long>     PositionsClosed        { get; }
    public Histogram<double> PositionPnL            { get; }

    // ── ML ──────────────────────────────────────────────────────────────────
    public Counter<long>     MLTrainingRuns         { get; }
    public Counter<long>     MLTrainingFailures     { get; }
    public Counter<long>     MLPromotions           { get; }
    public Histogram<double> MLTrainingDurationSecs { get; }
    public Histogram<double> MLScoringLatencyMs     { get; }
    public Counter<long>     MLArchitectureSelected { get; }
    public Counter<long>     MLSelectorFallbackDepth { get; }
    public Counter<long>     MLSelectorTrendPenalty  { get; }
    public Counter<long>     MLAdwinModelsEvaluated { get; }
    public Counter<long>     MLAdwinModelsSkipped { get; }
    public Counter<long>     MLAdwinDriftsDetected { get; }
    public Counter<long>     MLAdwinRetrainingQueued { get; }
    public Counter<long>     MLAdwinFlagsCleared { get; }
    public Counter<long>     MLAdwinLockAttempts { get; }
    public Counter<long>     MLAdwinCyclesSkipped { get; }
    public Histogram<double> MLAdwinDetectedAccuracyDrop { get; }
    public Histogram<double> MLAdwinCycleDurationMs { get; }
    public Counter<long>     MLAdwinAlertsDispatched { get; }
    public Counter<long>     MLAdwinRetrainCooldownSkipped { get; }
    public Histogram<double> MLAdwinTimeSinceLastSuccessSec { get; }
    public Counter<long>     SlippageDriftCyclesSkipped { get; }
    public Counter<long>     SlippageDriftLockAttempts { get; }
    public Counter<long>     SlippageDriftsDetected { get; }
    public Counter<long>     SlippageDriftAlertsDispatched { get; }
    public Histogram<double> SlippageDriftRatio { get; }
    public Histogram<double> SlippageDriftCycleDurationMs { get; }
    public Histogram<double> SlippageDriftTimeSinceLastSuccessSec { get; }
    public Counter<long>     MLDriftAgreementCyclesSkipped { get; }
    public Counter<long>     MLDriftAgreementLockAttempts { get; }
    public Counter<long>     MLDriftAgreementAlertsDispatched { get; }
    public Histogram<double> MLDriftAgreementCounted { get; }
    public Histogram<double> MLDriftAgreementCycleDurationMs { get; }
    public Histogram<double> MLDriftAgreementTimeSinceLastSuccessSec { get; }
    public Counter<long>     MLCusumModelsEvaluated { get; }
    public Counter<long>     MLCusumModelsSkipped { get; }
    public Counter<long>     MLCusumDriftsDetected { get; }
    public Counter<long>     MLCusumRetrainingQueued { get; }
    public Counter<long>     MLCusumRetrainCooldownSkipped { get; }
    public Counter<long>     MLCusumLockAttempts { get; }
    public Counter<long>     MLCusumCyclesSkipped { get; }
    public Counter<long>     MLCusumAlertsDispatched { get; }
    public Histogram<double> MLCusumCycleDurationMs { get; }
    public Histogram<double> MLCusumTimeSinceLastSuccessSec { get; }
    public Counter<long>     MLMultiScaleModelsEvaluated { get; }
    public Counter<long>     MLMultiScaleModelsSkipped { get; }
    public Counter<long>     MLMultiScaleSuddenDrifts { get; }
    public Counter<long>     MLMultiScaleGradualDrifts { get; }
    public Counter<long>     MLMultiScaleRetrainingQueued { get; }
    public Counter<long>     MLMultiScaleRetrainCooldownSkipped { get; }
    public Counter<long>     MLMultiScaleLockAttempts { get; }
    public Counter<long>     MLMultiScaleCyclesSkipped { get; }
    public Counter<long>     MLMultiScaleAlertsDispatched { get; }
    public Histogram<double> MLMultiScaleCycleDurationMs { get; }
    public Histogram<double> MLMultiScaleTimeSinceLastSuccessSec { get; }
    public Counter<long>     MLDriftMonitorLockAttempts { get; }
    public Counter<long>     MLDriftMonitorCyclesSkipped { get; }
    public Histogram<double> MLDriftMonitorCycleDurationMs { get; }
    public Histogram<double> MLDriftMonitorTimeSinceLastSuccessSec { get; }
    public Counter<long>     MLDriftMonitorDriftsDetected { get; }
    public Counter<long>     MLDriftMonitorRetrainCooldownSkipped { get; }
    public Counter<long>     MLDriftMonitorAlertsDispatched { get; }
    public Counter<long>     MLDriftMonitorModelsEvaluated { get; }
    public Counter<long>     MLDriftMonitorModelsSkipped { get; }
    public Counter<long>     MLCovariateShiftModelsEvaluated { get; }
    public Counter<long>     MLCovariateShiftModelsSkipped { get; }
    public Counter<long>     MLCovariateShiftDetections { get; }
    public Counter<long>     MLCovariateShiftRetrainingQueued { get; }
    public Counter<long>     MLCovariateShiftRetrainingSkipped { get; }
    public Counter<long>     MLCovariateShiftLockAttempts { get; }
    public Counter<long>     MLCovariateShiftCyclesSkipped { get; }
    public Histogram<double> MLCovariateShiftWeightedPsi { get; }
    public Histogram<double> MLCovariateShiftMaxPsi { get; }
    public Histogram<double> MLCovariateShiftMultivariateScore { get; }
    public Histogram<double> MLCovariateShiftCycleDurationMs { get; }
    public Counter<long>     MLDataQualityPairsEvaluated { get; }
    public Counter<long>     MLDataQualityPairsSkipped { get; }
    public Counter<long>     MLDataQualityIssuesDetected { get; }
    public Counter<long>     MLDataQualityAlertsDispatched { get; }
    public Counter<long>     MLDataQualityAlertsResolved { get; }
    public Counter<long>     MLDataQualityLockAttempts { get; }
    public Counter<long>     MLDataQualityCyclesSkipped { get; }
    public Histogram<double> MLDataQualityGapAgeSeconds { get; }
    public Histogram<double> MLDataQualitySpikeZScore { get; }
    public Histogram<double> MLDataQualityLivePriceAgeSeconds { get; }
    public Histogram<double> MLDataQualityCycleDurationMs { get; }
    public Counter<long>     MLDeadLetterRunsScanned { get; }
    public Counter<long>     MLDeadLetterRunsRequeued { get; }
    public Counter<long>     MLDeadLetterRunsSkipped { get; }
    public Counter<long>     MLDeadLetterRetryCapsReached { get; }
    public Counter<long>     MLDeadLetterAlertsDispatched { get; }
    public Counter<long>     MLDeadLetterAlertsResolved { get; }
    public Counter<long>     MLDeadLetterRetryCountersReset { get; }
    public Counter<long>     MLDeadLetterLockAttempts { get; }
    public Counter<long>     MLDeadLetterCyclesSkipped { get; }
    public Histogram<double> MLDeadLetterCandidateAgeDays { get; }
    public Histogram<double> MLDeadLetterCycleDurationMs { get; }
    public Counter<long>     MLAdaptiveThresholdModelsEvaluated { get; }
    public Counter<long>     MLAdaptiveThresholdModelsUpdated { get; }
    public Counter<long>     MLAdaptiveThresholdModelsSkipped { get; }
    public Counter<long>     MLAdaptiveThresholdRegimeThresholdsPruned { get; }
    public Counter<long>     MLAdaptiveThresholdLockAttempts { get; }
    public Counter<long>     MLAdaptiveThresholdCyclesSkipped { get; }
    public Histogram<double> MLAdaptiveThresholdAppliedDrift { get; }
    public Histogram<double> MLAdaptiveThresholdCycleDurationMs { get; }
    public Counter<long>     MLAlertFatigueEvaluations { get; }
    public Counter<long>     MLAlertFatigueAlertTransitions { get; }
    public Counter<long>     MLAlertFatigueLockAttempts { get; }
    public Counter<long>     MLAlertFatigueCyclesSkipped { get; }
    public Histogram<double> MLAlertFatigueTriggeredAlerts { get; }
    public Histogram<double> MLAlertFatigueActiveAlerts { get; }
    public Histogram<double> MLAlertFatigueRemediatedRatio { get; }
    public Histogram<double> MLAlertFatigueCycleDurationMs { get; }
    public Counter<long>     MLCausalFeatureModelsEvaluated { get; }
    public Counter<long>     MLCausalFeatureModelsSkipped { get; }
    public Counter<long>     MLCausalFeatureAuditsWritten { get; }
    public Counter<long>     MLCausalFeatureLockAttempts { get; }
    public Counter<long>     MLCausalFeatureCyclesSkipped { get; }
    public Histogram<double> MLCausalFeatureCausalFeatures { get; }
    public Histogram<double> MLCausalFeatureResolvedSamples { get; }
    public Histogram<double> MLCausalFeatureCycleDurationMs { get; }
    public Counter<long>     MLCalibrationMonitorModelsEvaluated { get; }
    public Counter<long>     MLCalibrationMonitorModelsSkipped { get; }
    public Counter<long>     MLCalibrationMonitorAlertsDispatched { get; }
    public Counter<long>     MLCalibrationMonitorAlertTransitions { get; }
    public Counter<long>     MLCalibrationMonitorRetrainingQueued { get; }
    public Counter<long>     MLCalibrationMonitorLockAttempts { get; }
    public Counter<long>     MLCalibrationMonitorCyclesSkipped { get; }
    public Histogram<double> MLCalibrationMonitorCurrentEce { get; }
    public Histogram<double> MLCalibrationMonitorEceDelta { get; }
    public Histogram<double> MLCalibrationMonitorResolvedSamples { get; }
    public Histogram<double> MLCalibrationMonitorCycleDurationMs { get; }
    public Counter<long>     MLCalibratedEdgeModelsEvaluated { get; }
    public Counter<long>     MLCalibratedEdgeModelsSkipped { get; }
    public Counter<long>     MLCalibratedEdgeAlertsDispatched { get; }
    public Counter<long>     MLCalibratedEdgeAlertTransitions { get; }
    public Counter<long>     MLCalibratedEdgeRetrainingQueued { get; }
    public Counter<long>     MLCalibratedEdgeLockAttempts { get; }
    public Counter<long>     MLCalibratedEdgeCyclesSkipped { get; }
    public Histogram<double> MLCalibratedEdgeExpectedValuePips { get; }
    public Histogram<double> MLCalibratedEdgeResolvedSamples { get; }
    public Histogram<double> MLCalibratedEdgeCycleDurationMs { get; }
    public Counter<long>     MLAverageWeightInitSourceModelsEvaluated { get; }
    public Counter<long>     MLAverageWeightInitInitializersWritten { get; }
    public Counter<long>     MLAverageWeightInitInitializersSkipped { get; }
    public Counter<long>     MLAverageWeightInitLockAttempts { get; }
    public Counter<long>     MLAverageWeightInitCyclesSkipped { get; }
    public Histogram<double> MLAverageWeightInitSourceModelsPerInitializer { get; }
    public Histogram<double> MLAverageWeightInitCycleDurationMs { get; }
    public Counter<long>     MLArchitectureRotationContextsEvaluated { get; }
    public Counter<long>     MLArchitectureRotationRunsQueued { get; }
    public Counter<long>     MLArchitectureRotationArchitecturesSkipped { get; }
    public Counter<long>     MLArchitectureRotationLockAttempts { get; }
    public Counter<long>     MLArchitectureRotationCyclesSkipped { get; }
    public Histogram<double> MLArchitectureRotationQueuedRunsPerCycle { get; }
    public Histogram<double> MLArchitectureRotationCycleDurationMs { get; }
    public Counter<long>     MLConformalCalibrationModelsEvaluated { get; }
    public Counter<long>     MLConformalCalibrationModelsSkipped { get; }
    public Counter<long>     MLConformalCalibrationWritten { get; }
    public Counter<long>     MLConformalCalibrationLockAttempts { get; }
    public Counter<long>     MLConformalCalibrationCyclesSkipped { get; }
    public Histogram<double> MLConformalCalibrationSamples { get; }
    public Histogram<double> MLConformalCalibrationEmpiricalCoverage { get; }
    public Histogram<double> MLConformalCalibrationAmbiguousRate { get; }
    public Histogram<double> MLConformalCalibrationCycleDurationMs { get; }
    public Counter<long>     MLConformalBreakerModelsEvaluated { get; }
    public Counter<long>     MLConformalBreakerModelsSkipped { get; }
    public Counter<long>     MLConformalBreakerTrips { get; }
    public Counter<long>     MLConformalBreakerRecoveries { get; }
    public Counter<long>     MLConformalBreakerRefreshes { get; }
    public Counter<long>     MLConformalBreakerLockAttempts { get; }
    public Counter<long>     MLConformalBreakerDuplicateRepairs { get; }
    public Counter<long>     MLConformalBreakerAlertsDispatched { get; }
    public Counter<long>     MLConformalBreakerAlertDispatchFailures { get; }
    public Counter<long>     MLConformalBreakerCyclesSkipped { get; }
    public Histogram<double> MLConformalBreakerThresholdMismatchRate { get; }
    public Histogram<double> MLConformalBreakerEmpiricalCoverage { get; }
    public Histogram<double> MLConformalBreakerActive { get; }
    public Histogram<double> MLConformalBreakerCycleDurationMs { get; }
    public Counter<long>     MLCorrelatedFailureModelsEvaluated { get; }
    public Counter<long>     MLCorrelatedFailureModelsFailing { get; }
    public Counter<long>     MLCorrelatedFailureModelsSkipped { get; }
    public Counter<long>     MLCorrelatedFailurePauseActivations { get; }
    public Counter<long>     MLCorrelatedFailurePauseRecoveries { get; }
    public Counter<long>     MLCorrelatedFailureLockAttempts { get; }
    public Counter<long>     MLCorrelatedFailureCooldownSkips { get; }
    public Counter<long>     MLCorrelatedFailureCyclesSkipped { get; }
    public Histogram<double> MLCorrelatedFailureRatio { get; }
    public Histogram<double> MLCorrelatedFailureAffectedSymbols { get; }
    public Histogram<double> MLCorrelatedFailureCycleDurationMs { get; }
    public Counter<long>     MLCorrelatedSignalConflictConflictsDetected { get; }
    public Counter<long>     MLCorrelatedSignalConflictAlertsUpserted { get; }
    public Counter<long>     MLCorrelatedSignalConflictAlertsResolved { get; }
    public Counter<long>     MLCorrelatedSignalConflictSignalsRejected { get; }
    public Counter<long>     MLCorrelatedSignalConflictCyclesSkipped { get; }
    public Counter<long>     MLCorrelatedSignalConflictLockAttempts { get; }
    public Histogram<double> MLCorrelatedSignalConflictCycleDurationMs { get; }
    public Counter<long>     MLErgodicityModelsEvaluated { get; }
    public Counter<long>     MLErgodicityModelsSkipped { get; }
    public Counter<long>     MLErgodicityLogsWritten { get; }
    public Counter<long>     MLErgodicityLockAttempts { get; }
    public Histogram<double> MLErgodicityGap { get; }
    public Histogram<double> MLErgodicityAdjustedKelly { get; }
    public Histogram<double> MLErgodicityGrowthVariance { get; }
    public Counter<long>     MLFeatureConsensusSnapshots { get; }
    public Counter<long>     MLFeatureConsensusPairsSkipped { get; }
    public Counter<long>     MLFeatureConsensusModelRejects { get; }
    public Counter<long>     MLFeatureConsensusLockAttempts { get; }
    public Histogram<double> MLFeatureConsensusContributors { get; }
    public Histogram<double> MLFeatureConsensusMeanKendallTau { get; }
    public Counter<long>     MLCpcCandidates { get; }
    public Counter<long>     MLCpcCandidatesThrottled { get; }
    public Counter<long>     MLCpcPromotions { get; }
    public Counter<long>     MLCpcRejections { get; }
    public Counter<long>     MLCpcLockAttempts { get; }
    public Histogram<double> MLCpcLockAcquisitionMs { get; }
    public Histogram<double> MLCpcTrainingDurationMs { get; }
    public Histogram<double> MLCpcSequences { get; }
    public Histogram<double> MLCpcCandles { get; }
    public Histogram<double> MLCpcValidationLoss { get; }
    public Histogram<double> MLCpcValidationEmbeddingL2Norm { get; }
    public Histogram<double> MLCpcValidationEmbeddingVariance { get; }
    public Histogram<double> MLCpcDownstreamProbeBalancedAccuracy { get; }
    public Histogram<double> MLCpcRepresentationCentroidDistance { get; }
    public Histogram<double> MLCpcRepresentationMeanPsi { get; }
    public Histogram<double> MLCpcArchitectureSwitchAccuracyDelta { get; }
    public Histogram<double> MLCpcAdversarialValidationAuc { get; }
    public Counter<long>     MLCpcStaleEncoders { get; }
    public Counter<long>     MLCpcConfigurationDriftAlerts { get; }

    // ── Workers ─────────────────────────────────────────────────────────────
    public Histogram<double> WorkerCycleDurationMs  { get; }
    public Counter<long>     WorkerErrors           { get; }
    public Counter<long>     DrawdownEmergencySnapshots { get; }
    public Counter<long>     EaCommandsPushed       { get; }
    public Counter<long>     EaCommandPushFailures  { get; }
    public Counter<long>     EaCommandsExpired      { get; }
    public Histogram<double> EaCommandQueueLatencyMs { get; }
    public Histogram<double> EaCommandPushSendDurationMs { get; }
    public Histogram<double> EaCommandPushBacklogDepth { get; }
    public Counter<long>     EaInstancesDisconnected { get; }
    public Counter<long>     EaSymbolsReassigned { get; }
    public Counter<long>     EaCoordinatorFailovers { get; }
    public Counter<long>     EaAvailabilityTransitions { get; }
    public Histogram<double> EaActiveInstanceCount { get; }
    public Histogram<double> EaStaleInstanceCount { get; }
    public Histogram<double> EaDisconnectedHeartbeatAgeSeconds { get; }
    public Counter<long>     ExecutionQualityStrategiesEvaluated { get; }
    public Counter<long>     ExecutionQualityBreaches { get; }
    public Counter<long>     ExecutionQualityPauses { get; }
    public Counter<long>     ExecutionQualityResumes { get; }
    public Counter<long>     ExecutionQualityWarnings { get; }
    public Counter<long>     ExecutionQualityInsufficientFreshDataSkips { get; }
    public Histogram<double> ExecutionQualityAvgAbsoluteSlippagePips { get; }
    public Histogram<double> ExecutionQualityAvgLatencyMs { get; }
    public Histogram<double> ExecutionQualityAvgFillRate { get; }
    public Histogram<double> ExecutionQualityCycleDurationMs { get; }
    public Counter<long>     FeatureSchemaBackfillModelsSeen { get; }
    public Counter<long>     FeatureSchemaBackfillModelsUpdated { get; }
    public Counter<long>     FeatureSchemaBackfillModelsUnresolved { get; }
    public Counter<long>     FeatureSchemaBackfillLockAttempts { get; }
    public Counter<long>     FeatureSchemaBackfillCyclesSkipped { get; }
    public Histogram<double> FeatureSchemaBackfillCycleDurationMs { get; }
    public Counter<long>     FeatureStoreBackfillCandlesEvaluated { get; }
    public Counter<long>     FeatureStoreBackfillVectorsWritten { get; }
    public Counter<long>     FeatureStoreBackfillCandlesSkipped { get; }
    public Counter<long>     FeatureStoreBackfillLineageWrites { get; }
    public Counter<long>     FeatureStoreBackfillLockAttempts { get; }
    public Counter<long>     FeatureStoreBackfillCyclesSkipped { get; }
    public Histogram<double> FeatureStoreBackfillPendingCandles { get; }
    public Histogram<double> FeatureStoreBackfillCycleDurationMs { get; }
    public Counter<long>     FeaturePrecomputePairsEvaluated { get; }
    public Counter<long>     FeaturePrecomputeVectorsWritten { get; }
    public Counter<long>     FeaturePrecomputePairsSkipped { get; }
    public Counter<long>     FeaturePrecomputeLineageWrites { get; }
    public Counter<long>     FeaturePrecomputeLockAttempts { get; }
    public Counter<long>     FeaturePrecomputeCyclesSkipped { get; }
    public Histogram<double> FeaturePrecomputeCatchUpBars { get; }
    public Histogram<double> FeaturePrecomputePendingVectors { get; }
    public Histogram<double> FeaturePrecomputeCycleDurationMs { get; }
    public Counter<long>     IntradayAttributionAccountsEvaluated { get; }
    public Counter<long>     IntradayAttributionSnapshotsInserted { get; }
    public Counter<long>     IntradayAttributionSnapshotsUpdated { get; }
    public Counter<long>     IntradayAttributionLockAttempts { get; }
    public Counter<long>     IntradayAttributionCyclesSkipped { get; }
    public Histogram<double> IntradayAttributionCycleDurationMs { get; }
    public Counter<long>     LatencySlaAlertTransitions { get; }
    public Counter<long>     LatencySlaLockAttempts { get; }
    public Counter<long>     LatencySlaCyclesSkipped { get; }
    public Histogram<double> LatencySlaObservedP99Ms { get; }
    public Histogram<double> LatencySlaCycleDurationMs { get; }

    // ── Candle Aggregation ──────────────────────────────────────────────────
    public Counter<long>     CandlesSynthesized     { get; }

    // ── Broker PnL Reconciliation ───────────────────────────────────────────
    public Histogram<double> BrokerReconciliationVariance { get; }
    public Counter<long>     BrokerReconciliationOutcomes { get; }
    public Histogram<double> BrokerReconciliationSnapshotAgeSeconds { get; }

    // ── Integration Event Retry ─────────────────────────────────────────
    public Counter<long>     EventRetrySuccesses    { get; }
    public Counter<long>     EventRetryExhausted    { get; }
    public Counter<long>     EventRetryDeadLettered { get; }
    public Counter<long>     EventRetryLockAttempts { get; }
    public Counter<long>     EventRetryCyclesSkipped { get; }
    public Histogram<double> EventRetryBacklogDepth { get; }
    public Histogram<double> EventRetryCycleDurationMs { get; }

    // ── Degradation & Dead-Letter ─────────────────────────────────────────
    public Counter<long>     DegradationTransitions     { get; }
    public Counter<long>     DeadLetterSinkDbFailures   { get; }
    public Counter<long>     DeadLetterSinkFileWrites   { get; }
    public Counter<long>     DeadLetterSinkBufferOverflows { get; }
    public Histogram<double> DeadLetterSinkLatencyMs    { get; }

    // ── Strategy Generation ─────────────────────────────────────────────
    public Counter<long>     StrategyCandidatesScreened { get; }
    public Counter<long>     StrategyCandidatesCreated  { get; }
    public Counter<long>     StrategyCandidatesPruned   { get; }
    public Counter<long>     StrategyGenSymbolsSkipped  { get; }
    public Counter<long>     StrategyGenRegimeConfidenceSkipped { get; }
    public Counter<long>     StrategyGenRegimeTransitionSkipped { get; }
    public Counter<long>     StrategyGenDegradationRejected     { get; }
    public Counter<long>     StrategyGenEquityCurveRejected     { get; }
    public Counter<long>     StrategyGenTimeConcentrationRejected { get; }
    public Counter<long>     StrategyGenCorrelationSkipped      { get; }
    public Counter<long>     StrategyGenWalkForwardRejected     { get; }
    public Counter<long>     StrategyGenCircuitBreakerTripped   { get; }
    public Counter<long>     StrategyGenFeedbackBoosted         { get; }
    public Counter<long>     StrategyGenMonteCarloRejected      { get; }
    public Counter<long>     StrategyGenPortfolioDrawdownFiltered { get; }
    public Counter<long>     StrategyGenPortfolioExposureFiltered { get; }
    public Counter<long>     StrategyGenAdaptiveThresholdsApplied { get; }
    public Counter<long>     StrategyGenWeekendSkipped           { get; }
    public Counter<long>     StrategyGenOosDrawdownDegraded      { get; }
    public Counter<long>     StrategyGenReserveSpreadSkipped     { get; }
    public Counter<long>     StrategyGenScreeningRejections     { get; }
    public Counter<long>     StrategyGenCandleCacheEvictions    { get; }
    public Counter<long>     StrategyGenFeedbackAdaptiveContradictions { get; }
    public Counter<long>     StrategyGenTypeFaultDisabled       { get; }
    public Counter<long>     StrategyGenCompensationCleanupFailures { get; }
    public Histogram<double> StrategyGenScreeningDurationMs    { get; }
    public Counter<long>     EvolutionaryCandidatesProposed    { get; }
    public Counter<long>     EvolutionaryCandidatesInserted    { get; }
    public Counter<long>     EvolutionaryCandidatesSkipped     { get; }
    public Counter<long>     EvolutionaryBacktestsQueued       { get; }
    public Histogram<double> EvolutionaryCycleDurationMs       { get; }

    // ── Optimization ────────────────────────────────────────────────────
    public Counter<long>     OptimizationRunsProcessed  { get; }
    public Counter<long>     OptimizationRunsFailed     { get; }
    public Counter<long>     OptimizationCandidatesScreened { get; }
    public Counter<long>     OptimizationAutoApproved   { get; }
    public Counter<long>     OptimizationAutoRejected   { get; }
    public Counter<long>     OptimizationCheckpointRestored { get; }
    public Counter<long>     OptimizationCheckpointSaveFailures { get; }
    public Counter<long>     OptimizationLeaseReclaims { get; }
    public Counter<long>     OptimizationDuplicateFollowUpsPrevented { get; }
    public Counter<long>     OptimizationRunsDeferred   { get; }
    public Counter<long>     OptimizationFollowUpFailures { get; }
    public Counter<long>     OptimizationFollowUpDeferredChecks { get; }
    public Counter<long>     OptimizationFollowUpRepairs { get; }
    public Histogram<double> OptimizationFollowUpQueueAgeMs { get; }
    public Histogram<double> OptimizationApprovalToFollowUpCreationMs { get; }
    public Histogram<double> OptimizationCompletionPublicationLagMs { get; }
    public Histogram<double> OptimizationCompletionReplayWaitMs { get; }
    public Counter<long>     OptimizationCompletionReplayAttempts { get; }
    public Counter<long>     OptimizationCompletionReplayFailures { get; }
    public Histogram<double> OptimizationSurrogateImprovement { get; }
    public Counter<long>     OptimizationSurrogateBatchHits { get; }
    public Counter<long>     OptimizationSurrogateBatchMisses { get; }
    public Counter<long>     OptimizationEarlyStopSavings { get; }
    public Counter<long>     OptimizationParameterSpaceExhausted { get; }
    public Counter<long>     OptimizationCircuitBreakerTrips { get; }
    public Counter<long>     OptimizationGateRejections { get; }
    public Histogram<double> OptimizationGateDurationMs { get; }
    public Histogram<double> OptimizationComputeSeconds { get; }
    public Histogram<double> OptimizationPhaseDurationMs { get; }
    public Histogram<double> OptimizationCycleDurationMs { get; }
    public Histogram<double> OptimizationClaimLatencyMs { get; }
    public Histogram<double> OptimizationQueueWaitAtClaimMs { get; }
    public Histogram<double> OptimizationActiveProcessingSlots { get; }
    public Histogram<double> OptimizationProcessingSlotUtilization { get; }
    public Histogram<double> EhviHypervolumeProgress { get; }
    public Histogram<double> EhviGpPredictionError { get; }
    public Counter<long>     OptimizationSurrogateClamps { get; }
    public Counter<long>     OptimizationDeferredRechecks { get; }
    public Counter<long>     HyperbandBracketsExecuted { get; }
    public Counter<long>     HyperbandBracketsSkipped { get; }
    public Histogram<double> HyperbandSurvivalRate { get; }

    // ── News Sentiment ──────────────────────────────────────────────────
    public Counter<long>     NewsSentimentCallsTotal { get; }
    public Counter<long>     NewsSentimentCallErrors { get; }
    public Counter<long>     NewsSentimentHeadlinesProcessed { get; }
    public Counter<long>     NewsSentimentEventsProcessed { get; }
    public Histogram<double> NewsSentimentCycleDurationMs { get; }
    public Histogram<double> DeepSeekApiLatencyMs { get; }
    public Counter<long>     DeepSeekTokensUsed { get; }

    // ── Candle Aggregator ──────────────────────────────────────────────
    public Counter<long>     CandlesClosed          { get; }
    public Counter<long>     CandlesSynthetic       { get; }
    public Counter<long>     CandleTicksDropped     { get; }
    public Counter<long>     CandleSlotsPurged      { get; }
    public Counter<long>     CandleTicksSpreadRejected { get; }

    // ── Economic Calendar ────────────────────────────────────────────────────
    public Counter<long>     EconEventsIngested     { get; }
    public Counter<long>     EconActualsPatched     { get; }
    public Counter<long>     EconFeedErrors         { get; }
    public Counter<long>     EconCircuitBreakerSkips { get; }
    public Counter<long>     EconAlertTransitions   { get; }
    public Histogram<double> EconCycleDurationMs    { get; }
    public Histogram<double> EconPendingActualsBacklog { get; }

    // ── Economic Calendar Feed ──────────────────────────────────────────────
    public Counter<long>     EconFeedFetches        { get; }
    public Counter<long>     EconFeedCacheHits      { get; }
    public Counter<long>     EconFeedParseFailures  { get; }
    public Histogram<double> EconFeedFetchLatencyMs  { get; }

    // ── Economic Calendar (observable) ───────────────────────────────────────

    /// <summary>
    /// Registers an observable gauge that reports the number of consecutive polling cycles
    /// where the economic calendar feed returned zero events. Useful for alerting on feed
    /// degradation. Call once from the worker constructor.
    /// </summary>
    public void RegisterEconEmptyFetchGauge(Func<long> observeValue)
    {
        _meter.CreateObservableGauge(
            "trading.econ_calendar.consecutive_empty_fetches",
            observeValue,
            "cycles",
            "Consecutive polling cycles where the feed returned zero events");
    }

    // ── Tick Ingestion ───────────────────────────────────────────────────────
    public Counter<long>     TicksIngested          { get; }
    public Histogram<double> TickBatchSize          { get; }
    public Histogram<double> TickIngestionLatencyMs { get; }

    // ── Cache ───────────────────────────────────────────────────────────────
    public Counter<long>     PriceCacheMisses       { get; }

    // ── Simulated Broker ─────────────────────────────────────────────────
    public Counter<long>     SimBrokerFills         { get; }
    public Counter<long>     SimBrokerRejects       { get; }
    public Counter<long>     SimBrokerRequotes      { get; }
    public Counter<long>     SimBrokerDisconnects   { get; }
    public Counter<long>     SimBrokerStopOuts      { get; }
    public Counter<long>     SimBrokerMarginCalls   { get; }
    public Counter<long>     SimBrokerSlTpCloses    { get; }
    public Counter<long>     SimBrokerPendingFills  { get; }
    public Counter<long>     SimBrokerExpiredOrders { get; }
    public Histogram<double> SimBrokerMarginLevel   { get; }
    public Histogram<double> SimBrokerRealizedPnl   { get; }

    public TradingMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        // Orders
        OrdersSubmitted    = _meter.CreateCounter<long>("trading.orders.submitted", "orders", "Orders submitted to broker");
        OrdersFilled       = _meter.CreateCounter<long>("trading.orders.filled", "orders", "Orders filled by broker");
        OrdersFailed       = _meter.CreateCounter<long>("trading.orders.failed", "orders", "Orders that failed submission");
        OrderFillLatencyMs = _meter.CreateHistogram<double>("trading.orders.fill_latency", "ms", "Broker fill latency");
        OrderSlippagePips  = _meter.CreateHistogram<double>("trading.orders.slippage", "pips", "Fill slippage in pips");

        // Signals
        SignalsGenerated = _meter.CreateCounter<long>("trading.signals.generated", "signals", "Trade signals generated by strategies");
        SignalsAccepted  = _meter.CreateCounter<long>("trading.signals.accepted", "signals", "Signals that passed risk checks");
        SignalsRejected  = _meter.CreateCounter<long>("trading.signals.rejected", "signals", "Signals blocked by risk checks");
        SignalsFiltered  = _meter.CreateCounter<long>("trading.signals.filtered", "signals", "Signals filtered by pipeline stages (MTF, correlation, Hawkes, regime)");
        EvaluatorRejections = _meter.CreateCounter<long>("trading.signals.evaluator_rejections", "signals", "Signals rejected by evaluator-level filters (tag: evaluator, filter)");
        SignalsSuppressed = _meter.CreateCounter<long>("trading.signals.suppressed", "signals", "Signals suppressed by ML scoring or abstention");
        SignalCooldownSkips = _meter.CreateCounter<long>("trading.signals.cooldown_skips", "signals", "Signals skipped due to per-strategy cooldown");
        TicksDroppedLockBusy = _meter.CreateCounter<long>("trading.signals.ticks_dropped_lock_busy", "ticks", "Ticks dropped because strategy evaluation lock was busy");
        TicksDroppedBackpressure = _meter.CreateCounter<long>("trading.signals.ticks_dropped_backpressure", "ticks", "Ticks dropped because evaluation channel is full (backpressure)");
        TicksSkippedNoEA = _meter.CreateCounter<long>("trading.signals.ticks_skipped_no_ea", "ticks", "Ticks skipped because no active EA instance owns the symbol");
        TicksSkippedHealthCritical = _meter.CreateCounter<long>("trading.signals.ticks_skipped_health_critical", "ticks", "Strategies skipped because health status is Critical");
        TicksSkippedStale = _meter.CreateCounter<long>("trading.signals.ticks_skipped_stale", "ticks", "Ticks dropped because event timestamp exceeded MaxTickAgeSeconds");
        TicksSkippedNoLivePrice = _meter.CreateCounter<long>("trading.signals.ticks_skipped_no_live_price", "ticks", "Per-strategy evaluations skipped because live price cache had no entry for the symbol");
        StrategiesCircuitBroken = _meter.CreateCounter<long>("trading.signals.strategies_circuit_broken", "strategies", "Strategies temporarily disabled due to consecutive evaluation failures");
        StrategyEvaluationMs = _meter.CreateHistogram<double>("trading.strategy.evaluation_duration", "ms", "Strategy evaluation pipeline duration per tick");
        SignalDedupLatencyMs = _meter.CreateHistogram<double>("trading.signals.dedup_latency", "ms", "Tier 1 signal dedup latency (in-process + DB atomic mark). Tagged with outcome=accepted|duplicate.");
        SignalDedupDuplicates = _meter.CreateCounter<long>("trading.signals.dedup_duplicates", "signals", "Duplicate signal deliveries caught by Tier 1 dedup. Tagged with layer=in_process|cross_instance.");
        EvaluatorMissing = _meter.CreateCounter<long>("trading.signals.evaluator_missing", "events", "Tick-time dispatches where no IStrategyEvaluator is registered for the strategy's StrategyType. Tagged with strategy_type.");
        PrefetchQueryTimeouts = _meter.CreateCounter<long>("trading.signals.prefetch_query_timeouts", "events", "Per-tick pre-fetch DB queries that exceeded their timeout (fail-closed path). Tagged with query={backtest_qualification|strategy_metrics|regime}.");
        StrategyMetricsCacheHits = _meter.CreateCounter<long>("trading.strategy_metrics_cache.hits", "lookups", "In-memory strategy-metrics cache hits on the hot tick path.");
        StrategyMetricsCacheMisses = _meter.CreateCounter<long>("trading.strategy_metrics_cache.misses", "lookups", "In-memory strategy-metrics cache misses that triggered a DB refresh.");
        StrategyMetricsCacheInvalidations = _meter.CreateCounter<long>("trading.strategy_metrics_cache.invalidations", "events", "Strategy-metrics cache entries invalidated by integration events. Tagged with trigger={backtest_completed|strategy_activated}.");
        SignalTtlExtendedMarketClosed = _meter.CreateCounter<long>("trading.signals.ttl_extended_market_closed", "signals", "Signals whose ExpiresAt was extended to cover a market-closed window before the next open. Tagged with symbol.");
        MLModelStaleRejections = _meter.CreateCounter<long>("trading.signals.ml_model_stale_rejections", "signals", "Signals rejected because their associated MLModel is no longer live (retired, suppressed, or inactive). Tagged with ml_model_id|symbol.");
        MLScoringTimeouts = _meter.CreateCounter<long>("trading.ml.scoring_timeouts", "events", "IMLSignalScorer.ScoreAsync invocations that exceeded MLScoringTimeoutSeconds and were treated as a fail-closed ML error. Tagged with symbol|strategy_id.");
        Tier2PriceDriftRejections = _meter.CreateCounter<long>("trading.signals.tier2_price_drift_rejections", "signals", "Tier-2 rejections because |live_mid − signal.EntryPrice| / EntryPrice exceeded MaxEntryPriceDriftPct. Tagged with symbol.");
        SignalExpirySkewToleranceApplied = _meter.CreateCounter<long>("trading.signals.expiry_skew_tolerance_applied", "signals", "Signals that would have been rejected as expired absent ClockSkewToleranceSeconds but were preserved by the tolerance window. Tagged with stage={tier1|tier2|reentry}.");
        MarketRegimeCacheHits = _meter.CreateCounter<long>("trading.market_regime_cache.hits", "lookups", "Cross-tick market regime cache hits.");
        MarketRegimeCacheMisses = _meter.CreateCounter<long>("trading.market_regime_cache.misses", "lookups", "Cross-tick market regime cache misses that triggered a DB refresh.");
        EngineConfigCacheHits = _meter.CreateCounter<long>("trading.engine_config_cache.hits", "lookups", "EngineConfig cache hits on the hot tick path.");
        EngineConfigCacheMisses = _meter.CreateCounter<long>("trading.engine_config_cache.misses", "lookups", "EngineConfig cache misses that triggered a DB refresh.");
        EngineConfigExpiredEntries = _meter.CreateCounter<long>("trading.engine_config_expiry.expired_entries", "entries", "Explicit expiry-managed EngineConfig rows soft-deleted by EngineConfigExpiryWorker.");
        EngineConfigStaleMetricsBlocksPruned = _meter.CreateCounter<long>("trading.engine_config_expiry.stale_metrics_blocks_pruned", "blocks", "Stale MLMetrics blocks soft-deleted by EngineConfigExpiryWorker.");
        EngineConfigStaleMetricsEntriesPruned = _meter.CreateCounter<long>("trading.engine_config_expiry.stale_metrics_entries_pruned", "entries", "MLMetrics EngineConfig rows soft-deleted as part of stale-block pruning.");
        EngineConfigExpiryCycleDurationMs = _meter.CreateHistogram<double>("trading.engine_config_expiry.cycle_duration_ms", "ms", "EngineConfigExpiryWorker cycle duration.");
        KillSwitchTriggered = _meter.CreateCounter<long>("trading.kill_switch.triggered", "events", "Decisions short-circuited by an active kill switch. Tagged with scope={global|strategy} and site={strategy_worker|signal_bridge}.");
        CircuitBreakerTransitions = _meter.CreateCounter<long>("trading.circuit_breaker.transitions", "transitions", "External-service circuit breaker state transitions. Tagged with service and state.");
        CircuitBreakerShortCircuits = _meter.CreateCounter<long>("trading.circuit_breaker.short_circuits", "events", "Calls skipped because the circuit breaker was open. Tagged with service.");
        DbBulkheadWaits = _meter.CreateCounter<long>("trading.db_bulkhead.waits", "events", "Callers that had to wait for an IDbOperationBulkhead slot. Tagged with group.");
        DbBulkheadWaitMs = _meter.CreateHistogram<double>("trading.db_bulkhead.wait_ms", "ms", "Time spent waiting for a DB-bulkhead slot. Tagged with group.");
        MLScoringBatchCalls = _meter.CreateCounter<long>("trading.ml.scoring_batch_calls", "calls", "IMLSignalScorer.ScoreBatchAsync invocations by StrategyWorker. Tagged with batch_size={1..N}.");
        MLScoringBatchSize = _meter.CreateHistogram<double>("trading.ml.scoring_batch_size", "signals", "Number of signals per batched ML scoring call.");
        ConflictResolutionEarlyExits = _meter.CreateCounter<long>("trading.signals.conflict_resolution_early_exits", "candidates", "Candidate signals skipped by pre-score filtering before expensive per-strategy work ran.");
        EaReconciliationDrift = _meter.CreateCounter<long>("trading.ea.reconciliation_drift", "events", "Drift findings observed while evaluating rolling EA reconciliation windows. Tagged with kind={orphaned_engine_positions|unknown_broker_positions|mismatched_positions|orphaned_engine_orders|unknown_broker_orders}.");
        EaReconciliationMeanDriftPerRun = _meter.CreateHistogram<double>("trading.ea.reconciliation_mean_drift_per_run", "drift", "Mean total drift per run for each EA instance evaluated by the reconciliation monitor. Tagged with state={breached|healthy}.");
        EaReconciliationWindowRunCount = _meter.CreateHistogram<double>("trading.ea.reconciliation_window_run_count", "runs", "Number of reconciliation runs contributing to a per-instance rolling-window evaluation. Tagged with state={breached|healthy}.");
        EaReconciliationAlertTransitions = _meter.CreateCounter<long>("trading.ea.reconciliation_alert_transitions", "alerts", "Reconciliation alert notifications and resolutions. Tagged with transition={dispatched|resolved}.");
        RetentionRowsDeleted = _meter.CreateCounter<long>("trading.retention.rows_deleted", "rows", "Rows deleted by AuditRetentionWorker. Tagged with table.");
        StrategyLockAcquisitionMs = _meter.CreateHistogram<double>("trading.strategy.lock_acquisition_ms", "ms", "Wall-clock time spent inside IDistributedLock.TryAcquireAsync for strategy evaluation. Tagged with outcome={acquired|busy}.");
        RegimeParamsCacheHits = _meter.CreateCounter<long>("trading.strategy.regime_params_cache.hits", "lookups", "StrategyRegimeParams cache hits on the hot tick path.");
        RegimeParamsCacheMisses = _meter.CreateCounter<long>("trading.strategy.regime_params_cache.misses", "lookups", "StrategyRegimeParams cache misses that triggered a DB refresh.");
        SignalRejectionsAudited = _meter.CreateCounter<long>("trading.signals.rejections_audited", "rejections", "Rejections written to the SignalRejectionAudit table. Tagged with stage and reason.");
        CalibrationSnapshotsWritten = _meter.CreateCounter<long>("trading.calibration.snapshots_written", "snapshots", "CalibrationSnapshot rows written by CalibrationSnapshotWorker. Tagged with period.");

        WalkForwardFoldRejections = _meter.CreateCounter<long>("trading.walk_forward.fold_rejections", "runs", "Walk-forward runs rejected. Tagged with reason=min_in_sample_days|min_out_of_sample_days|min_candles_per_fold|min_trades_per_fold.");
        PromotionGateRejections = _meter.CreateCounter<long>("trading.strategy_promotion.gate_rejections", "strategies", "Strategies rejected by promotion gates. Tagged with gate=live_vs_backtest_sharpe|critical_snapshot|insufficient_snapshots|insufficient_healthy.");

        // Positions
        PositionsOpened = _meter.CreateCounter<long>("trading.positions.opened", "positions", "Positions opened");
        PositionsClosed = _meter.CreateCounter<long>("trading.positions.closed", "positions", "Positions closed");
        PositionPnL     = _meter.CreateHistogram<double>("trading.positions.pnl", "USD", "Realized P&L per closed position");

        // ML
        MLTrainingRuns         = _meter.CreateCounter<long>("trading.ml.training_runs", "runs", "ML training runs started");
        MLTrainingFailures     = _meter.CreateCounter<long>("trading.ml.training_failures", "runs", "ML training runs that failed");
        MLPromotions           = _meter.CreateCounter<long>("trading.ml.promotions", "promotions", "ML models promoted to active");
        MLTrainingDurationSecs = _meter.CreateHistogram<double>("trading.ml.training_duration", "s", "ML training duration");
        MLScoringLatencyMs     = _meter.CreateHistogram<double>("trading.ml.scoring_latency", "ms", "ML signal scoring latency");
        MLArchitectureSelected  = _meter.CreateCounter<long>("trading.ml.architecture_selected", "selections", "ML architecture selections by TrainerSelector");
        MLSelectorFallbackDepth = _meter.CreateCounter<long>("trading.ml.selector_fallback_depth", "selections", "TrainerSelector fallback depth reached per selection");
        MLSelectorTrendPenalty  = _meter.CreateCounter<long>("trading.ml.selector_trend_penalty", "penalties", "Architecture score penalised due to declining performance trend");
        MLAdwinModelsEvaluated = _meter.CreateCounter<long>("trading.ml.adwin.models_evaluated", "models", "Active ML models evaluated by MLAdwinDriftWorker.");
        MLAdwinModelsSkipped = _meter.CreateCounter<long>("trading.ml.adwin.models_skipped", "models", "ML models skipped by MLAdwinDriftWorker, tagged by reason.");
        MLAdwinDriftsDetected = _meter.CreateCounter<long>("trading.ml.adwin.drifts_detected", "drifts", "Statistically significant degrading ADWIN drift detections.");
        MLAdwinRetrainingQueued = _meter.CreateCounter<long>("trading.ml.adwin.retraining_queued", "runs", "Retraining runs queued by MLAdwinDriftWorker.");
        MLAdwinFlagsCleared = _meter.CreateCounter<long>("trading.ml.adwin.flags_cleared", "flags", "ADWIN drift flags expired or cleared after healthy evaluations.");
        MLAdwinLockAttempts = _meter.CreateCounter<long>("trading.ml.adwin.lock_attempts", "cycles", "Distributed-lock attempts by MLAdwinDriftWorker, tagged by outcome=acquired|busy|unavailable.");
        MLAdwinCyclesSkipped = _meter.CreateCounter<long>("trading.ml.adwin.cycles_skipped", "cycles", "MLAdwinDriftWorker cycles skipped without processing, tagged by reason={disabled|lock_busy}.");
        MLAdwinDetectedAccuracyDrop = _meter.CreateHistogram<double>("trading.ml.adwin.detected_accuracy_drop", "ratio", "Accuracy drop observed on degrading ADWIN detections.");
        MLAdwinAlertsDispatched = _meter.CreateCounter<long>("trading.ml.adwin.alerts_dispatched", "alerts", "ADWIN drift alerts dispatched via IAlertDispatcher (post-dedupe).");
        MLAdwinRetrainCooldownSkipped = _meter.CreateCounter<long>("trading.ml.adwin.retrain_cooldown_skipped", "decisions", "Retrain attempts suppressed because the model was retrained within the cooldown window.");
        MLAdwinTimeSinceLastSuccessSec = _meter.CreateHistogram<double>("trading.ml.adwin.time_since_last_success_seconds", "s", "Seconds since the last successful MLAdwinDriftWorker cycle, recorded each cycle attempt.");
        SlippageDriftCyclesSkipped = _meter.CreateCounter<long>("trading.slippage_drift.cycles_skipped", "cycles", "SlippageDriftWorker cycles skipped, tagged by reason={disabled|lock_busy}.");
        SlippageDriftLockAttempts = _meter.CreateCounter<long>("trading.slippage_drift.lock_attempts", "cycles", "Distributed-lock attempts by SlippageDriftWorker, tagged by outcome=acquired|busy|unavailable.");
        SlippageDriftsDetected = _meter.CreateCounter<long>("trading.slippage_drift.detected", "drifts", "Per-symbol slippage drift detections beyond the configured threshold.");
        SlippageDriftAlertsDispatched = _meter.CreateCounter<long>("trading.slippage_drift.alerts_dispatched", "alerts", "Slippage drift alerts dispatched via IAlertDispatcher (post-dedupe).");
        SlippageDriftRatio = _meter.CreateHistogram<double>("trading.slippage_drift.ratio", "ratio", "Per-cycle recent/baseline slippage ratio per symbol.");
        SlippageDriftCycleDurationMs = _meter.CreateHistogram<double>("trading.slippage_drift.cycle_duration_ms", "ms", "SlippageDriftWorker cycle duration.");
        SlippageDriftTimeSinceLastSuccessSec = _meter.CreateHistogram<double>("trading.slippage_drift.time_since_last_success_seconds", "s", "Seconds since the last successful SlippageDriftWorker cycle, recorded each cycle attempt.");
        MLDriftAgreementCyclesSkipped = _meter.CreateCounter<long>("trading.ml.drift_agreement.cycles_skipped", "cycles", "MLDriftAgreementWorker cycles skipped, tagged by reason={disabled|lock_busy}.");
        MLDriftAgreementLockAttempts = _meter.CreateCounter<long>("trading.ml.drift_agreement.lock_attempts", "cycles", "Distributed-lock attempts by MLDriftAgreementWorker, tagged by outcome=acquired|busy|unavailable.");
        MLDriftAgreementAlertsDispatched = _meter.CreateCounter<long>("trading.ml.drift_agreement.alerts_dispatched", "alerts", "Drift-agreement alerts dispatched, tagged by kind=consensus|anomaly.");
        MLDriftAgreementCounted = _meter.CreateHistogram<double>("trading.ml.drift_agreement.counted", "detectors", "Number of agreeing drift detectors per (symbol, timeframe) per cycle.");
        MLDriftAgreementCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.drift_agreement.cycle_duration_ms", "ms", "MLDriftAgreementWorker cycle duration.");
        MLDriftAgreementTimeSinceLastSuccessSec = _meter.CreateHistogram<double>("trading.ml.drift_agreement.time_since_last_success_seconds", "s", "Seconds since last successful MLDriftAgreementWorker cycle.");
        MLCusumModelsEvaluated = _meter.CreateCounter<long>("trading.ml.cusum.models_evaluated", "models", "Active ML models evaluated by MLCusumDriftWorker.");
        MLCusumModelsSkipped = _meter.CreateCounter<long>("trading.ml.cusum.models_skipped", "models", "ML models skipped by MLCusumDriftWorker, tagged by reason.");
        MLCusumDriftsDetected = _meter.CreateCounter<long>("trading.ml.cusum.drifts_detected", "drifts", "CUSUM degradation drifts detected.");
        MLCusumRetrainingQueued = _meter.CreateCounter<long>("trading.ml.cusum.retraining_queued", "runs", "Retraining runs queued by MLCusumDriftWorker.");
        MLCusumRetrainCooldownSkipped = _meter.CreateCounter<long>("trading.ml.cusum.retrain_cooldown_skipped", "decisions", "CUSUM retrain attempts suppressed by cooldown window.");
        MLCusumLockAttempts = _meter.CreateCounter<long>("trading.ml.cusum.lock_attempts", "cycles", "Distributed-lock attempts by MLCusumDriftWorker.");
        MLCusumCyclesSkipped = _meter.CreateCounter<long>("trading.ml.cusum.cycles_skipped", "cycles", "MLCusumDriftWorker cycles skipped, tagged by reason.");
        MLCusumAlertsDispatched = _meter.CreateCounter<long>("trading.ml.cusum.alerts_dispatched", "alerts", "CUSUM drift alerts dispatched (post-dedupe).");
        MLCusumCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.cusum.cycle_duration_ms", "ms", "MLCusumDriftWorker cycle duration.");
        MLCusumTimeSinceLastSuccessSec = _meter.CreateHistogram<double>("trading.ml.cusum.time_since_last_success_seconds", "s", "Seconds since last successful MLCusumDriftWorker cycle.");
        MLMultiScaleModelsEvaluated = _meter.CreateCounter<long>("trading.ml.multiscale.models_evaluated", "models", "Active ML models evaluated by MLMultiScaleDriftWorker.");
        MLMultiScaleModelsSkipped = _meter.CreateCounter<long>("trading.ml.multiscale.models_skipped", "models", "ML models skipped by MLMultiScaleDriftWorker, tagged by reason.");
        MLMultiScaleSuddenDrifts = _meter.CreateCounter<long>("trading.ml.multiscale.sudden_drifts", "drifts", "Sudden multi-scale drifts detected.");
        MLMultiScaleGradualDrifts = _meter.CreateCounter<long>("trading.ml.multiscale.gradual_drifts", "drifts", "Gradual multi-scale drifts detected.");
        MLMultiScaleRetrainingQueued = _meter.CreateCounter<long>("trading.ml.multiscale.retraining_queued", "runs", "Retraining runs queued by MLMultiScaleDriftWorker.");
        MLMultiScaleRetrainCooldownSkipped = _meter.CreateCounter<long>("trading.ml.multiscale.retrain_cooldown_skipped", "decisions", "Multi-scale retrain attempts suppressed by cooldown window.");
        MLMultiScaleLockAttempts = _meter.CreateCounter<long>("trading.ml.multiscale.lock_attempts", "cycles", "Distributed-lock attempts by MLMultiScaleDriftWorker.");
        MLMultiScaleCyclesSkipped = _meter.CreateCounter<long>("trading.ml.multiscale.cycles_skipped", "cycles", "MLMultiScaleDriftWorker cycles skipped, tagged by reason.");
        MLMultiScaleAlertsDispatched = _meter.CreateCounter<long>("trading.ml.multiscale.alerts_dispatched", "alerts", "Multi-scale drift alerts dispatched (post-dedupe).");
        MLMultiScaleCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.multiscale.cycle_duration_ms", "ms", "MLMultiScaleDriftWorker cycle duration.");
        MLMultiScaleTimeSinceLastSuccessSec = _meter.CreateHistogram<double>("trading.ml.multiscale.time_since_last_success_seconds", "s", "Seconds since last successful MLMultiScaleDriftWorker cycle.");
        MLDriftMonitorLockAttempts = _meter.CreateCounter<long>("trading.ml.drift_monitor.lock_attempts", "cycles", "Distributed-lock attempts by MLDriftMonitorWorker.");
        MLDriftMonitorCyclesSkipped = _meter.CreateCounter<long>("trading.ml.drift_monitor.cycles_skipped", "cycles", "MLDriftMonitorWorker cycles skipped, tagged by reason.");
        MLDriftMonitorCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.drift_monitor.cycle_duration_ms", "ms", "MLDriftMonitorWorker cycle duration.");
        MLDriftMonitorTimeSinceLastSuccessSec = _meter.CreateHistogram<double>("trading.ml.drift_monitor.time_since_last_success_seconds", "s", "Seconds since last successful MLDriftMonitorWorker cycle.");
        MLDriftMonitorDriftsDetected = _meter.CreateCounter<long>("trading.ml.drift_monitor.drifts_detected", "drifts", "Drift detections by MLDriftMonitorWorker, tagged by trigger=AccuracyDrift|CalibrationDrift|DisagreementDrift|RelativeDegradation|SharpeDrift|MultiSignal.");
        MLDriftMonitorRetrainCooldownSkipped = _meter.CreateCounter<long>("trading.ml.drift_monitor.retrain_cooldown_skipped", "decisions", "Retrain attempts suppressed by the cooldown window in MLDriftMonitorWorker.");
        MLDriftMonitorAlertsDispatched = _meter.CreateCounter<long>("trading.ml.drift_monitor.alerts_dispatched", "alerts", "Drift-monitor alerts dispatched via IAlertDispatcher (post-dedupe).");
        MLDriftMonitorModelsEvaluated = _meter.CreateCounter<long>("trading.ml.drift_monitor.models_evaluated", "models", "Active ML models evaluated by MLDriftMonitorWorker each cycle.");
        MLDriftMonitorModelsSkipped = _meter.CreateCounter<long>("trading.ml.drift_monitor.models_skipped", "models", "Models skipped by MLDriftMonitorWorker, tagged by reason.");
        MLCovariateShiftModelsEvaluated = _meter.CreateCounter<long>("trading.ml.covariate_shift.models_evaluated", "models", "Active ML models evaluated by MLCovariateShiftWorker.");
        MLCovariateShiftModelsSkipped = _meter.CreateCounter<long>("trading.ml.covariate_shift.models_skipped", "models", "ML models skipped by MLCovariateShiftWorker, tagged by reason.");
        MLCovariateShiftDetections = _meter.CreateCounter<long>("trading.ml.covariate_shift.detections", "drifts", "Covariate shift detections by MLCovariateShiftWorker.");
        MLCovariateShiftRetrainingQueued = _meter.CreateCounter<long>("trading.ml.covariate_shift.retraining_queued", "runs", "Retraining runs queued by MLCovariateShiftWorker.");
        MLCovariateShiftRetrainingSkipped = _meter.CreateCounter<long>("trading.ml.covariate_shift.retraining_skipped", "decisions", "Covariate-shift retraining decisions suppressed, tagged by reason.");
        MLCovariateShiftLockAttempts = _meter.CreateCounter<long>("trading.ml.covariate_shift.lock_attempts", "cycles", "Distributed-lock attempts by MLCovariateShiftWorker, tagged by outcome=acquired|busy|unavailable.");
        MLCovariateShiftCyclesSkipped = _meter.CreateCounter<long>("trading.ml.covariate_shift.cycles_skipped", "cycles", "MLCovariateShiftWorker cycles skipped without processing, tagged by reason.");
        MLCovariateShiftWeightedPsi = _meter.CreateHistogram<double>("trading.ml.covariate_shift.weighted_psi", "psi", "Importance-weighted PSI per evaluated model.");
        MLCovariateShiftMaxPsi = _meter.CreateHistogram<double>("trading.ml.covariate_shift.max_psi", "psi", "Maximum single-feature PSI per evaluated model.");
        MLCovariateShiftMultivariateScore = _meter.CreateHistogram<double>("trading.ml.covariate_shift.multivariate_score", "score", "Mean squared z-score per evaluated model.");
        MLCovariateShiftCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.covariate_shift.cycle_duration_ms", "ms", "MLCovariateShiftWorker cycle duration.");
        MLDataQualityPairsEvaluated = _meter.CreateCounter<long>("trading.ml.data_quality.pairs_evaluated", "pairs", "Active symbol/timeframe feeds evaluated by MLDataQualityWorker.");
        MLDataQualityPairsSkipped = _meter.CreateCounter<long>("trading.ml.data_quality.pairs_skipped", "pairs", "Active symbol/timeframe feeds skipped by MLDataQualityWorker, tagged by reason.");
        MLDataQualityIssuesDetected = _meter.CreateCounter<long>("trading.ml.data_quality.issues_detected", "issues", "Data-quality issues detected by MLDataQualityWorker, tagged by reason.");
        MLDataQualityAlertsDispatched = _meter.CreateCounter<long>("trading.ml.data_quality.alerts_dispatched", "alerts", "Data-quality alerts dispatched by MLDataQualityWorker after cooldown checks.");
        MLDataQualityAlertsResolved = _meter.CreateCounter<long>("trading.ml.data_quality.alerts_resolved", "alerts", "Worker-owned data-quality alerts auto-resolved after the condition cleared.");
        MLDataQualityLockAttempts = _meter.CreateCounter<long>("trading.ml.data_quality.lock_attempts", "cycles", "Distributed-lock attempts by MLDataQualityWorker, tagged by outcome=acquired|busy|unavailable.");
        MLDataQualityCyclesSkipped = _meter.CreateCounter<long>("trading.ml.data_quality.cycles_skipped", "cycles", "MLDataQualityWorker cycles skipped without processing, tagged by reason.");
        MLDataQualityGapAgeSeconds = _meter.CreateHistogram<double>("trading.ml.data_quality.gap_age_seconds", "s", "Age of the latest closed candle per evaluated active feed.");
        MLDataQualitySpikeZScore = _meter.CreateHistogram<double>("trading.ml.data_quality.spike_z_score", "z", "Latest-close rolling z-score per evaluated active feed.");
        MLDataQualityLivePriceAgeSeconds = _meter.CreateHistogram<double>("trading.ml.data_quality.live_price_age_seconds", "s", "Age of the latest persisted live price snapshot per active symbol.");
        MLDataQualityCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.data_quality.cycle_duration_ms", "ms", "MLDataQualityWorker cycle duration.");
        MLDeadLetterRunsScanned = _meter.CreateCounter<long>("trading.ml.dead_letter.runs_scanned", "runs", "Eligible failed MLTrainingRun rows scanned by MLDeadLetterWorker.");
        MLDeadLetterRunsRequeued = _meter.CreateCounter<long>("trading.ml.dead_letter.runs_requeued", "runs", "Dead-lettered MLTrainingRun rows safely reset to Queued.");
        MLDeadLetterRunsSkipped = _meter.CreateCounter<long>("trading.ml.dead_letter.runs_skipped", "runs", "Eligible failed MLTrainingRun rows skipped by MLDeadLetterWorker, tagged by reason.");
        MLDeadLetterRetryCapsReached = _meter.CreateCounter<long>("trading.ml.dead_letter.retry_caps_reached", "runs", "Dead-letter candidates whose configured recovery retry cap has been reached.");
        MLDeadLetterAlertsDispatched = _meter.CreateCounter<long>("trading.ml.dead_letter.alerts_dispatched", "alerts", "Retry-cap alerts dispatched by MLDeadLetterWorker after cooldown checks.");
        MLDeadLetterAlertsResolved = _meter.CreateCounter<long>("trading.ml.dead_letter.alerts_resolved", "alerts", "Retry-cap alerts auto-resolved after successful retraining.");
        MLDeadLetterRetryCountersReset = _meter.CreateCounter<long>("trading.ml.dead_letter.retry_counters_reset", "counters", "Per-pair dead-letter retry counters reset after successful retraining.");
        MLDeadLetterLockAttempts = _meter.CreateCounter<long>("trading.ml.dead_letter.lock_attempts", "cycles", "Distributed-lock attempts by MLDeadLetterWorker, tagged by outcome=acquired|busy|unavailable.");
        MLDeadLetterCyclesSkipped = _meter.CreateCounter<long>("trading.ml.dead_letter.cycles_skipped", "cycles", "MLDeadLetterWorker cycles skipped without processing, tagged by reason.");
        MLDeadLetterCandidateAgeDays = _meter.CreateHistogram<double>("trading.ml.dead_letter.candidate_age_days", "d", "Age in days of eligible failed training runs scanned by MLDeadLetterWorker.");
        MLDeadLetterCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.dead_letter.cycle_duration_ms", "ms", "MLDeadLetterWorker cycle duration.");
        MLAdwinCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.adwin.cycle_duration_ms", "ms", "MLAdwinDriftWorker cycle duration.");
        MLAdaptiveThresholdModelsEvaluated = _meter.CreateCounter<long>("trading.ml.adaptive_threshold.models_evaluated", "models", "ML models evaluated by MLAdaptiveThresholdWorker.");
        MLAdaptiveThresholdModelsUpdated = _meter.CreateCounter<long>("trading.ml.adaptive_threshold.models_updated", "models", "ML models whose adaptive-threshold snapshot was updated.");
        MLAdaptiveThresholdModelsSkipped = _meter.CreateCounter<long>("trading.ml.adaptive_threshold.models_skipped", "models", "ML models skipped by MLAdaptiveThresholdWorker, tagged by reason.");
        MLAdaptiveThresholdRegimeThresholdsPruned = _meter.CreateCounter<long>("trading.ml.adaptive_threshold.regime_thresholds_pruned", "thresholds", "Stale or invalid regime-specific thresholds removed by MLAdaptiveThresholdWorker.");
        MLAdaptiveThresholdLockAttempts = _meter.CreateCounter<long>("trading.ml.adaptive_threshold.lock_attempts", "cycles", "Distributed-lock attempts by MLAdaptiveThresholdWorker, tagged by outcome=acquired|busy|unavailable.");
        MLAdaptiveThresholdCyclesSkipped = _meter.CreateCounter<long>("trading.ml.adaptive_threshold.cycles_skipped", "cycles", "MLAdaptiveThresholdWorker cycles skipped without processing, tagged by reason={disabled|lock_busy}.");
        MLAdaptiveThresholdAppliedDrift = _meter.CreateHistogram<double>("trading.ml.adaptive_threshold.applied_drift", "threshold", "Absolute threshold drift applied by MLAdaptiveThresholdWorker, tagged by scope={global|regime}.");
        MLAdaptiveThresholdCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.adaptive_threshold.cycle_duration_ms", "ms", "MLAdaptiveThresholdWorker cycle duration.");
        MLAlertFatigueEvaluations = _meter.CreateCounter<long>("trading.ml.alert_fatigue.evaluations", "cycles", "MLAlertFatigueWorker evaluation cycles that processed a recent ML degradation alert window.");
        MLAlertFatigueAlertTransitions = _meter.CreateCounter<long>("trading.ml.alert_fatigue.alert_transitions", "alerts", "ML alert-fatigue alert transitions, tagged by transition={dispatched|resolved}.");
        MLAlertFatigueLockAttempts = _meter.CreateCounter<long>("trading.ml.alert_fatigue.lock_attempts", "cycles", "Distributed-lock attempts by MLAlertFatigueWorker, tagged by outcome=acquired|busy|unavailable.");
        MLAlertFatigueCyclesSkipped = _meter.CreateCounter<long>("trading.ml.alert_fatigue.cycles_skipped", "cycles", "MLAlertFatigueWorker cycles skipped without processing, tagged by reason={disabled|lock_busy}.");
        MLAlertFatigueTriggeredAlerts = _meter.CreateHistogram<double>("trading.ml.alert_fatigue.triggered_alerts", "alerts", "Recently triggered ML degradation alerts inside the fatigue analysis window.");
        MLAlertFatigueActiveAlerts = _meter.CreateHistogram<double>("trading.ml.alert_fatigue.active_alerts", "alerts", "Recent ML degradation alerts still active inside the fatigue analysis window.");
        MLAlertFatigueRemediatedRatio = _meter.CreateHistogram<double>("trading.ml.alert_fatigue.remediated_ratio", "ratio", "Remediated / triggered ratio for recent ML degradation alerts.");
        MLAlertFatigueCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.alert_fatigue.cycle_duration_ms", "ms", "MLAlertFatigueWorker cycle duration.");
        MLCausalFeatureModelsEvaluated = _meter.CreateCounter<long>("trading.ml.causal_feature.models_evaluated", "models", "Active ML models evaluated by MLCausalFeatureWorker.");
        MLCausalFeatureModelsSkipped = _meter.CreateCounter<long>("trading.ml.causal_feature.models_skipped", "models", "Active ML models skipped by MLCausalFeatureWorker. Tagged with reason.");
        MLCausalFeatureAuditsWritten = _meter.CreateCounter<long>("trading.ml.causal_feature.audits_written", "audits", "MLCausalFeatureAudit rows written by MLCausalFeatureWorker.");
        MLCausalFeatureLockAttempts = _meter.CreateCounter<long>("trading.ml.causal_feature.lock_attempts", "cycles", "Distributed-lock attempts by MLCausalFeatureWorker, tagged by outcome=acquired|busy|unavailable.");
        MLCausalFeatureCyclesSkipped = _meter.CreateCounter<long>("trading.ml.causal_feature.cycles_skipped", "cycles", "MLCausalFeatureWorker cycles skipped without processing, tagged by reason={disabled|lock_busy|no_active_models}.");
        MLCausalFeatureCausalFeatures = _meter.CreateHistogram<double>("trading.ml.causal_feature.causal_features", "features", "Count of features classified as Granger-causal in a single MLCausalFeatureWorker audit.");
        MLCausalFeatureResolvedSamples = _meter.CreateHistogram<double>("trading.ml.causal_feature.resolved_samples", "samples", "Resolved prediction-log rows contributing to a single MLCausalFeatureWorker audit.");
        MLCausalFeatureCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.causal_feature.cycle_duration_ms", "ms", "MLCausalFeatureWorker cycle duration.");
        MLCalibrationMonitorModelsEvaluated = _meter.CreateCounter<long>("trading.ml.calibration_monitor.models_evaluated", "models", "Active ML models evaluated by MLCalibrationMonitorWorker. Tagged with state={healthy|warning|critical}.");
        MLCalibrationMonitorModelsSkipped = _meter.CreateCounter<long>("trading.ml.calibration_monitor.models_skipped", "models", "Active ML models skipped by MLCalibrationMonitorWorker. Tagged with reason.");
        MLCalibrationMonitorAlertsDispatched = _meter.CreateCounter<long>("trading.ml.calibration_monitor.alerts_dispatched", "alerts", "Calibration-monitor alerts dispatched successfully. Tagged with state={warning|critical}.");
        MLCalibrationMonitorAlertTransitions = _meter.CreateCounter<long>("trading.ml.calibration_monitor.alert_transitions", "alerts", "Calibration-monitor alert transitions, tagged with transition={dispatched|resolved}.");
        MLCalibrationMonitorRetrainingQueued = _meter.CreateCounter<long>("trading.ml.calibration_monitor.retraining_queued", "runs", "Auto-degrading retraining runs queued by MLCalibrationMonitorWorker.");
        MLCalibrationMonitorLockAttempts = _meter.CreateCounter<long>("trading.ml.calibration_monitor.lock_attempts", "cycles", "Distributed-lock attempts by MLCalibrationMonitorWorker, tagged by outcome=acquired|busy|unavailable.");
        MLCalibrationMonitorCyclesSkipped = _meter.CreateCounter<long>("trading.ml.calibration_monitor.cycles_skipped", "cycles", "MLCalibrationMonitorWorker cycles skipped without processing, tagged by reason={disabled|lock_busy|no_active_models}.");
        MLCalibrationMonitorCurrentEce = _meter.CreateHistogram<double>("trading.ml.calibration_monitor.current_ece", "ece", "Live Expected Calibration Error recorded by MLCalibrationMonitorWorker. Tagged with state={healthy|warning|critical}.");
        MLCalibrationMonitorEceDelta = _meter.CreateHistogram<double>("trading.ml.calibration_monitor.ece_delta", "ece", "ECE delta recorded by MLCalibrationMonitorWorker. Tagged with source={trend|baseline}.");
        MLCalibrationMonitorResolvedSamples = _meter.CreateHistogram<double>("trading.ml.calibration_monitor.resolved_samples", "samples", "Resolved prediction count contributing to a single MLCalibrationMonitorWorker evaluation.");
        MLCalibrationMonitorCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.calibration_monitor.cycle_duration_ms", "ms", "MLCalibrationMonitorWorker cycle duration.");
        MLCalibratedEdgeModelsEvaluated = _meter.CreateCounter<long>("trading.ml.calibrated_edge.models_evaluated", "models", "Active ML models evaluated by MLCalibratedEdgeWorker. Tagged with state={healthy|warning|critical}.");
        MLCalibratedEdgeModelsSkipped = _meter.CreateCounter<long>("trading.ml.calibrated_edge.models_skipped", "models", "Active ML models skipped by MLCalibratedEdgeWorker. Tagged with reason.");
        MLCalibratedEdgeAlertsDispatched = _meter.CreateCounter<long>("trading.ml.calibrated_edge.alerts_dispatched", "alerts", "Calibrated-edge degradation alerts dispatched successfully. Tagged with state={warning|critical}.");
        MLCalibratedEdgeAlertTransitions = _meter.CreateCounter<long>("trading.ml.calibrated_edge.alert_transitions", "alerts", "Calibrated-edge alert transitions, tagged with transition={dispatched|resolved}.");
        MLCalibratedEdgeRetrainingQueued = _meter.CreateCounter<long>("trading.ml.calibrated_edge.retraining_queued", "runs", "Auto-degrading retraining runs queued by MLCalibratedEdgeWorker.");
        MLCalibratedEdgeLockAttempts = _meter.CreateCounter<long>("trading.ml.calibrated_edge.lock_attempts", "cycles", "Distributed-lock attempts by MLCalibratedEdgeWorker, tagged by outcome=acquired|busy|unavailable.");
        MLCalibratedEdgeCyclesSkipped = _meter.CreateCounter<long>("trading.ml.calibrated_edge.cycles_skipped", "cycles", "MLCalibratedEdgeWorker cycles skipped without processing, tagged by reason={disabled|lock_busy|no_active_models}.");
        MLCalibratedEdgeExpectedValuePips = _meter.CreateHistogram<double>("trading.ml.calibrated_edge.expected_value_pips", "pips", "Live realized calibrated edge recorded by MLCalibratedEdgeWorker. Tagged with state={healthy|warning|critical}.");
        MLCalibratedEdgeResolvedSamples = _meter.CreateHistogram<double>("trading.ml.calibrated_edge.resolved_samples", "samples", "Informative resolved prediction-log count contributing to one calibrated-edge evaluation.");
        MLCalibratedEdgeCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.calibrated_edge.cycle_duration_ms", "ms", "MLCalibratedEdgeWorker cycle duration.");
        MLAverageWeightInitSourceModelsEvaluated = _meter.CreateCounter<long>("trading.ml.avg_weight_init.source_models_evaluated", "models", "Qualified source models evaluated by MLAverageWeightInitWorker.");
        MLAverageWeightInitInitializersWritten = _meter.CreateCounter<long>("trading.ml.avg_weight_init.initializers_written", "models", "Average-weight initializer models written by MLAverageWeightInitWorker.");
        MLAverageWeightInitInitializersSkipped = _meter.CreateCounter<long>("trading.ml.avg_weight_init.initializers_skipped", "initializers", "Average-weight initializer candidates skipped by MLAverageWeightInitWorker, tagged by reason.");
        MLAverageWeightInitLockAttempts = _meter.CreateCounter<long>("trading.ml.avg_weight_init.lock_attempts", "cycles", "Distributed-lock attempts by MLAverageWeightInitWorker, tagged by outcome=acquired|busy|unavailable.");
        MLAverageWeightInitCyclesSkipped = _meter.CreateCounter<long>("trading.ml.avg_weight_init.cycles_skipped", "cycles", "MLAverageWeightInitWorker cycles skipped without processing, tagged by reason={disabled|lock_busy|no_qualified_sources|no_compatible_sources|no_initializer_clusters}.");
        MLAverageWeightInitSourceModelsPerInitializer = _meter.CreateHistogram<double>("trading.ml.avg_weight_init.source_models_per_initializer", "models", "Compatible source model count used for a single average-weight initializer.");
        MLAverageWeightInitCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.avg_weight_init.cycle_duration_ms", "ms", "MLAverageWeightInitWorker cycle duration.");
        MLArchitectureRotationContextsEvaluated = _meter.CreateCounter<long>("trading.ml.architecture_rotation.contexts_evaluated", "contexts", "Active symbol/timeframe contexts evaluated by MLArchitectureRotationWorker.");
        MLArchitectureRotationRunsQueued = _meter.CreateCounter<long>("trading.ml.architecture_rotation.runs_queued", "runs", "Scheduled rotation training runs queued by MLArchitectureRotationWorker.");
        MLArchitectureRotationArchitecturesSkipped = _meter.CreateCounter<long>("trading.ml.architecture_rotation.architectures_skipped", "architectures", "Architecture evaluation skips inside MLArchitectureRotationWorker, tagged by reason.");
        MLArchitectureRotationLockAttempts = _meter.CreateCounter<long>("trading.ml.architecture_rotation.lock_attempts", "cycles", "Distributed-lock attempts by MLArchitectureRotationWorker, tagged by outcome=acquired|busy|unavailable.");
        MLArchitectureRotationCyclesSkipped = _meter.CreateCounter<long>("trading.ml.architecture_rotation.cycles_skipped", "cycles", "MLArchitectureRotationWorker cycles skipped without processing, tagged by reason={disabled|lock_busy|no_active_contexts|no_eligible_architectures}.");
        MLArchitectureRotationQueuedRunsPerCycle = _meter.CreateHistogram<double>("trading.ml.architecture_rotation.queued_runs_per_cycle", "runs", "Rotation training runs queued in a single MLArchitectureRotationWorker cycle.");
        MLArchitectureRotationCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.architecture_rotation.cycle_duration_ms", "ms", "MLArchitectureRotationWorker cycle duration.");
        MLConformalCalibrationModelsEvaluated = _meter.CreateCounter<long>("trading.ml.conformal_calibration.models_evaluated", "models", "Active ML models evaluated by MLConformalCalibrationWorker.");
        MLConformalCalibrationModelsSkipped = _meter.CreateCounter<long>("trading.ml.conformal_calibration.models_skipped", "models", "ML models skipped by MLConformalCalibrationWorker, tagged by reason.");
        MLConformalCalibrationWritten = _meter.CreateCounter<long>("trading.ml.conformal_calibration.written", "calibrations", "Persisted conformal calibration rows written by MLConformalCalibrationWorker.");
        MLConformalCalibrationLockAttempts = _meter.CreateCounter<long>("trading.ml.conformal_calibration.lock_attempts", "cycles", "Distributed-lock attempts by MLConformalCalibrationWorker, tagged by outcome=acquired|busy|unavailable.");
        MLConformalCalibrationCyclesSkipped = _meter.CreateCounter<long>("trading.ml.conformal_calibration.cycles_skipped", "cycles", "MLConformalCalibrationWorker cycles skipped without processing, tagged by reason={lock_busy|no_candidate_models}.");
        MLConformalCalibrationSamples = _meter.CreateHistogram<double>("trading.ml.conformal_calibration.samples", "samples", "Resolved prediction logs used for one conformal calibration.");
        MLConformalCalibrationEmpiricalCoverage = _meter.CreateHistogram<double>("trading.ml.conformal_calibration.empirical_coverage", "ratio", "Empirical coverage observed on the calibration evidence set.");
        MLConformalCalibrationAmbiguousRate = _meter.CreateHistogram<double>("trading.ml.conformal_calibration.ambiguous_rate", "ratio", "Share of calibration evidence whose conformal set would include both Buy and Sell.");
        MLConformalCalibrationCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.conformal_calibration.cycle_duration_ms", "ms", "MLConformalCalibrationWorker cycle duration.");
        MLConformalBreakerModelsEvaluated = _meter.CreateCounter<long>("trading.ml.conformal_breaker.models_evaluated", "models", "Active ML models evaluated by the conformal breaker");
        MLConformalBreakerModelsSkipped = _meter.CreateCounter<long>("trading.ml.conformal_breaker.models_skipped", "models", "ML models skipped by the conformal breaker, tagged by reason");
        MLConformalBreakerTrips = _meter.CreateCounter<long>("trading.ml.conformal_breaker.trips", "breakers", "Conformal breaker suppressions, tagged by trip reason");
        MLConformalBreakerRecoveries = _meter.CreateCounter<long>("trading.ml.conformal_breaker.recoveries", "breakers", "Conformal breaker recoveries that lifted active breaker state");
        MLConformalBreakerRefreshes = _meter.CreateCounter<long>("trading.ml.conformal_breaker.refreshes", "breakers", "Active conformal breaker diagnostic refreshes without extending suspension");
        MLConformalBreakerLockAttempts = _meter.CreateCounter<long>("trading.ml.conformal_breaker.lock_attempts", "cycles", "Conformal breaker distributed-lock attempts, tagged by outcome=acquired|busy|unavailable");
        MLConformalBreakerDuplicateRepairs = _meter.CreateCounter<long>("trading.ml.conformal_breaker.duplicate_repairs", "breakers", "Duplicate active conformal breaker rows deactivated by repair logic");
        MLConformalBreakerAlertsDispatched = _meter.CreateCounter<long>("trading.ml.conformal_breaker.alerts_dispatched", "alerts", "Conformal breaker alerts dispatched successfully");
        MLConformalBreakerAlertDispatchFailures = _meter.CreateCounter<long>("trading.ml.conformal_breaker.alert_dispatch_failures", "alerts", "Conformal breaker alert dispatch failures after state persistence");
        MLConformalBreakerCyclesSkipped = _meter.CreateCounter<long>("trading.ml.conformal_breaker.cycles_skipped", "cycles", "Conformal breaker cycles skipped without processing, tagged by reason={lock_busy|no_active_models}.");
        MLConformalBreakerThresholdMismatchRate = _meter.CreateHistogram<double>("trading.ml.conformal_breaker.threshold_mismatch_rate", "ratio", "Share of evaluated logs whose served conformal threshold differs from the current calibration threshold");
        MLConformalBreakerEmpiricalCoverage = _meter.CreateHistogram<double>("trading.ml.conformal_breaker.empirical_coverage", "ratio", "Empirical conformal coverage observed by the breaker");
        MLConformalBreakerActive = _meter.CreateHistogram<double>("trading.ml.conformal_breaker.active", "breakers", "Active conformal breaker count");
        MLConformalBreakerCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.conformal_breaker.cycle_duration_ms", "ms", "MLConformalBreakerWorker cycle duration.");
        MLCorrelatedFailureModelsEvaluated = _meter.CreateCounter<long>("trading.ml.correlated_failure.models_evaluated", "models", "Active ML models with enough outcomes evaluated for systemic correlated failure");
        MLCorrelatedFailureModelsFailing = _meter.CreateCounter<long>("trading.ml.correlated_failure.models_failing", "models", "Evaluated ML models below the configured drift accuracy threshold");
        MLCorrelatedFailureModelsSkipped = _meter.CreateCounter<long>("trading.ml.correlated_failure.models_skipped", "models", "Active ML models skipped by correlated failure evaluation, tagged by reason");
        MLCorrelatedFailurePauseActivations = _meter.CreateCounter<long>("trading.ml.correlated_failure.pause_activations", "pauses", "Systemic ML training pause activations");
        MLCorrelatedFailurePauseRecoveries = _meter.CreateCounter<long>("trading.ml.correlated_failure.pause_recoveries", "recoveries", "Systemic ML training pause recoveries");
        MLCorrelatedFailureLockAttempts = _meter.CreateCounter<long>("trading.ml.correlated_failure.lock_attempts", "cycles", "Correlated failure distributed-lock attempts, tagged by outcome=acquired|busy|unavailable");
        MLCorrelatedFailureCooldownSkips = _meter.CreateCounter<long>("trading.ml.correlated_failure.cooldown_skips", "cycles", "Correlated failure state changes skipped because the state-change cooldown is active");
        MLCorrelatedFailureCyclesSkipped = _meter.CreateCounter<long>("trading.ml.correlated_failure.cycles_skipped", "cycles", "Correlated failure cycles skipped without state evaluation or state change, tagged by reason");
        MLCorrelatedFailureRatio = _meter.CreateHistogram<double>("trading.ml.correlated_failure.failure_ratio", "ratio", "Share of evaluated ML models classified as failing");
        MLCorrelatedFailureAffectedSymbols = _meter.CreateHistogram<double>("trading.ml.correlated_failure.affected_symbols", "symbols", "Number of distinct symbols affected by failing ML models");
        MLCorrelatedFailureCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.correlated_failure.cycle_duration_ms", "ms", "MLCorrelatedFailureWorker cycle duration");
        MLCorrelatedSignalConflictConflictsDetected = _meter.CreateCounter<long>("trading.ml.correlated_signal_conflict.conflicts_detected", "conflicts", "Correlated approved-signal conflicts detected across configured pairs");
        MLCorrelatedSignalConflictAlertsUpserted = _meter.CreateCounter<long>("trading.ml.correlated_signal_conflict.alerts_upserted", "alerts", "Pair-specific correlated signal conflict alerts created or refreshed");
        MLCorrelatedSignalConflictAlertsResolved = _meter.CreateCounter<long>("trading.ml.correlated_signal_conflict.alerts_resolved", "alerts", "Pair-specific correlated signal conflict alerts auto-resolved after conflicts cleared");
        MLCorrelatedSignalConflictSignalsRejected = _meter.CreateCounter<long>("trading.ml.correlated_signal_conflict.signals_rejected", "signals", "Approved, not-yet-ordered signals rejected because they were part of a correlated conflict");
        MLCorrelatedSignalConflictCyclesSkipped = _meter.CreateCounter<long>("trading.ml.correlated_signal_conflict.cycles_skipped", "cycles", "Correlated signal conflict cycles skipped, tagged by reason");
        MLCorrelatedSignalConflictLockAttempts = _meter.CreateCounter<long>("trading.ml.correlated_signal_conflict.lock_attempts", "cycles", "Correlated signal conflict distributed-lock attempts, tagged by outcome=acquired|busy|unavailable");
        MLCorrelatedSignalConflictCycleDurationMs = _meter.CreateHistogram<double>("trading.ml.correlated_signal_conflict.cycle_duration_ms", "ms", "MLCorrelatedSignalConflictWorker cycle duration");
        MLErgodicityModelsEvaluated = _meter.CreateCounter<long>("trading.ml.ergodicity.models_evaluated", "models", "Active ML models with enough resolved outcomes evaluated for ergodicity economics");
        MLErgodicityModelsSkipped = _meter.CreateCounter<long>("trading.ml.ergodicity.models_skipped", "models", "Active ML models skipped by ergodicity evaluation, tagged by reason");
        MLErgodicityLogsWritten = _meter.CreateCounter<long>("trading.ml.ergodicity.logs_written", "logs", "MLErgodicityLog rows written");
        MLErgodicityLockAttempts = _meter.CreateCounter<long>("trading.ml.ergodicity.lock_attempts", "cycles", "Ergodicity distributed-lock attempts, tagged by outcome=acquired|busy|unavailable");
        MLErgodicityGap = _meter.CreateHistogram<double>("trading.ml.ergodicity.gap", "return", "Ergodicity gap between ensemble and time-average growth");
        MLErgodicityAdjustedKelly = _meter.CreateHistogram<double>("trading.ml.ergodicity.adjusted_kelly", "fraction", "Ergodicity-adjusted Kelly fraction");
        MLErgodicityGrowthVariance = _meter.CreateHistogram<double>("trading.ml.ergodicity.growth_variance", "variance", "Variance of per-outcome return proxies used by ergodicity metrics");
        MLFeatureConsensusSnapshots = _meter.CreateCounter<long>("trading.ml.feature_consensus.snapshots", "snapshots", "Feature consensus snapshots written. Tagged by symbol and timeframe.");
        MLFeatureConsensusPairsSkipped = _meter.CreateCounter<long>("trading.ml.feature_consensus.pairs_skipped", "pairs", "Feature consensus pairs skipped. Tagged by reason.");
        MLFeatureConsensusModelRejects = _meter.CreateCounter<long>("trading.ml.feature_consensus.model_rejects", "models", "Models excluded from feature consensus. Tagged by reason.");
        MLFeatureConsensusLockAttempts = _meter.CreateCounter<long>("trading.ml.feature_consensus.lock_attempts", "cycles", "Feature consensus distributed-lock attempts, tagged by outcome=acquired|busy|unavailable.");
        MLFeatureConsensusContributors = _meter.CreateHistogram<double>("trading.ml.feature_consensus.contributors", "models", "Contributing models per written feature consensus snapshot.");
        MLFeatureConsensusMeanKendallTau = _meter.CreateHistogram<double>("trading.ml.feature_consensus.mean_kendall_tau", "tau", "Mean pairwise Kendall tau-b rank agreement per feature consensus snapshot.");
        MLCpcCandidates = _meter.CreateCounter<long>("trading.ml.cpc.candidates", "pairs", "CPC encoder candidates considered for training. Tagged by symbol, timeframe, regime, encoder_type.");
        MLCpcCandidatesThrottled = _meter.CreateCounter<long>("trading.ml.cpc.candidates_throttled", "pairs", "CPC candidates that a cycle could not process because MaxPairsPerCycle was saturated. Tagged by encoder_type.");
        MLCpcPromotions = _meter.CreateCounter<long>("trading.ml.cpc.promotions", "encoders", "CPC encoders promoted. Tagged by symbol, timeframe, regime, encoder_type.");
        MLCpcRejections = _meter.CreateCounter<long>("trading.ml.cpc.rejections", "encoders", "CPC training attempts rejected. Tagged by reason, symbol, timeframe, regime, encoder_type.");
        MLCpcLockAttempts = _meter.CreateCounter<long>("trading.ml.cpc.lock_attempts", "attempts", "CPC per-candidate distributed-lock attempts. Tagged by outcome=acquired|busy, symbol, timeframe, regime, encoder_type.");
        MLCpcLockAcquisitionMs = _meter.CreateHistogram<double>("trading.ml.cpc.lock_acquisition_ms", "ms", "Wall-clock time spent acquiring the CPC per-candidate distributed lock. Tagged by outcome, symbol, timeframe, regime, encoder_type.");
        MLCpcTrainingDurationMs = _meter.CreateHistogram<double>("trading.ml.cpc.training_duration_ms", "ms", "CPC pretraining duration per candidate. Tagged by symbol, timeframe, regime, encoder_type.");
        MLCpcSequences = _meter.CreateHistogram<double>("trading.ml.cpc.sequences", "sequences", "CPC sequence counts. Tagged by split=train|validation and symbol/timeframe/regime.");
        MLCpcCandles = _meter.CreateHistogram<double>("trading.ml.cpc.candles", "candles", "CPC candle counts. Tagged by stage=loaded|regime_filtered and symbol/timeframe/regime.");
        MLCpcValidationLoss = _meter.CreateHistogram<double>("trading.ml.cpc.validation_loss", "loss", "Holdout contrastive loss for fitted CPC encoders. Tagged by symbol, timeframe, regime, encoder_type.");
        MLCpcValidationEmbeddingL2Norm = _meter.CreateHistogram<double>("trading.ml.cpc.validation_embedding_l2_norm", "norm", "Average L2 norm of holdout CPC embeddings. Tagged by symbol, timeframe, regime, encoder_type.");
        MLCpcValidationEmbeddingVariance = _meter.CreateHistogram<double>("trading.ml.cpc.validation_embedding_variance", "variance", "Mean per-dimension variance of holdout CPC embeddings. Tagged by symbol, timeframe, regime, encoder_type.");
        MLCpcDownstreamProbeBalancedAccuracy = _meter.CreateHistogram<double>("trading.ml.cpc.downstream_probe_balanced_accuracy", "ratio", "Balanced accuracy of the CPC holdout linear direction probe. Tagged by candidate=current|prior and symbol/timeframe/regime/encoder_type.");
        MLCpcRepresentationCentroidDistance = _meter.CreateHistogram<double>("trading.ml.cpc.representation_centroid_distance", "distance", "Cosine distance between candidate and prior CPC embedding centroids on the same holdout data (0=identical, 1=orthogonal). Tagged by symbol/timeframe/regime/encoder_type.");
        MLCpcRepresentationMeanPsi = _meter.CreateHistogram<double>("trading.ml.cpc.representation_mean_psi", "psi", "Mean per-dimension Population Stability Index between candidate and prior CPC embeddings. Tagged by symbol/timeframe/regime/encoder_type.");
        MLCpcArchitectureSwitchAccuracyDelta = _meter.CreateHistogram<double>("trading.ml.cpc.architecture_switch_accuracy_delta", "ratio", "Balanced-accuracy delta (candidate minus cross-architecture prior) on the same holdout data when a configured EncoderType change is in flight. Tagged by symbol/timeframe/regime/encoder_type.");
        MLCpcAdversarialValidationAuc = _meter.CreateHistogram<double>("trading.ml.cpc.adversarial_validation_auc", "auc", "Separability AUC of a linear classifier between candidate and prior CPC embeddings (1.0 = pathological drift). Tagged by symbol/timeframe/regime/encoder_type.");
        MLCpcStaleEncoders = _meter.CreateCounter<long>("trading.ml.cpc.stale_encoders", "encoders", "Active CPC encoders older than the stale-encoder SLO. Tagged by symbol, timeframe, regime, encoder_type.");
        MLCpcConfigurationDriftAlerts = _meter.CreateCounter<long>("trading.ml.cpc.configuration_drift_alerts", "alerts", "ConfigurationDrift alerts raised by the CPC worker. Tagged by kind=embedding_dim|pretrainer_missing|systemic_pause and encoder_type.");

        // Workers
        WorkerCycleDurationMs = _meter.CreateHistogram<double>("trading.workers.cycle_duration", "ms", "Worker poll cycle duration");
        WorkerErrors          = _meter.CreateCounter<long>("trading.workers.errors", "errors", "Unhandled worker errors");
        DrawdownEmergencySnapshots = _meter.CreateCounter<long>(
            "trading.drawdown.emergency_snapshots",
            "snapshots",
            "Out-of-cycle drawdown snapshots triggered by large realised losses. Tagged with worker and trigger.");
        EaCommandsPushed = _meter.CreateCounter<long>(
            "trading.ea.commands_pushed",
            "commands",
            "EA commands successfully pushed over the realtime bridge. Tagged with command_type.");
        EaCommandPushFailures = _meter.CreateCounter<long>(
            "trading.ea.command_push_failures",
            "commands",
            "EA command push attempts that failed after candidate selection. Tagged with reason and command_type.");
        EaCommandsExpired = _meter.CreateCounter<long>(
            "trading.ea.commands_expired",
            "commands",
            "EA commands auto-expired after exceeding the realtime/poll fallback TTL.");
        EaCommandQueueLatencyMs = _meter.CreateHistogram<double>(
            "trading.ea.command_queue_latency",
            "ms",
            "Time from EA command creation to successful realtime push. Tagged with command_type.");
        EaCommandPushSendDurationMs = _meter.CreateHistogram<double>(
            "trading.ea.command_push_send_duration",
            "ms",
            "Realtime bridge send duration per successfully pushed EA command. Tagged with command_type.");
        EaCommandPushBacklogDepth = _meter.CreateHistogram<double>(
            "trading.ea.command_push_backlog_depth",
            "commands",
            "Sampled backlog depth of push-eligible EA commands across currently connected instances.");
        EaInstancesDisconnected = _meter.CreateCounter<long>(
            "trading.ea.instances_disconnected",
            "instances",
            "EA instances marked disconnected after heartbeat timeout.");
        EaSymbolsReassigned = _meter.CreateCounter<long>(
            "trading.ea.symbols_reassigned",
            "symbols",
            "Symbol ownership transferred from stale EAs to active standby instances.");
        EaCoordinatorFailovers = _meter.CreateCounter<long>(
            "trading.ea.coordinator_failovers",
            "failovers",
            "Coordinator role failovers triggered after stale EA disconnect detection.");
        EaAvailabilityTransitions = _meter.CreateCounter<long>(
            "trading.ea.availability_transitions",
            "transitions",
            "Transitions into or out of the no-active-EA state. Tagged with transition=enter|recover.");
        EaActiveInstanceCount = _meter.CreateHistogram<double>(
            "trading.ea.active_instance_count",
            "instances",
            "Sampled count of active EA instances seen by the EA health monitor.");
        EaStaleInstanceCount = _meter.CreateHistogram<double>(
            "trading.ea.stale_instance_count",
            "instances",
            "Number of active EA instances found stale in a health-monitor cycle.");
        EaDisconnectedHeartbeatAgeSeconds = _meter.CreateHistogram<double>(
            "trading.ea.disconnected_heartbeat_age",
            "s",
            "Heartbeat age of EA instances at the moment they were marked disconnected.");
        ExecutionQualityStrategiesEvaluated = _meter.CreateCounter<long>(
            "trading.execution_quality.strategies_evaluated",
            "strategies",
            "Strategy execution-quality windows evaluated by ExecutionQualityCircuitBreakerWorker.");
        ExecutionQualityBreaches = _meter.CreateCounter<long>(
            "trading.execution_quality.breaches",
            "breaches",
            "Execution-quality threshold breaches observed by metric. Tagged with metric={slippage|latency|fill_rate}.");
        ExecutionQualityPauses = _meter.CreateCounter<long>(
            "trading.execution_quality.pauses",
            "strategies",
            "Strategies auto-paused by ExecutionQualityCircuitBreakerWorker.");
        ExecutionQualityResumes = _meter.CreateCounter<long>(
            "trading.execution_quality.resumes",
            "strategies",
            "Strategies auto-resumed after execution-quality recovery.");
        ExecutionQualityWarnings = _meter.CreateCounter<long>(
            "trading.execution_quality.warnings",
            "strategies",
            "Observation-only execution-quality warnings emitted while auto-pause is disabled.");
        ExecutionQualityInsufficientFreshDataSkips = _meter.CreateCounter<long>(
            "trading.execution_quality.insufficient_fresh_data_skips",
            "strategies",
            "Strategies skipped because there were not enough fresh fills inside the execution-quality lookback window.");
        ExecutionQualityAvgAbsoluteSlippagePips = _meter.CreateHistogram<double>(
            "trading.execution_quality.avg_abs_slippage_pips",
            "pips",
            "Per-strategy rolling-window average absolute slippage evaluated by ExecutionQualityCircuitBreakerWorker.");
        ExecutionQualityAvgLatencyMs = _meter.CreateHistogram<double>(
            "trading.execution_quality.avg_latency_ms",
            "ms",
            "Per-strategy rolling-window average fill latency using only positive latency samples.");
        ExecutionQualityAvgFillRate = _meter.CreateHistogram<double>(
            "trading.execution_quality.avg_fill_rate",
            "ratio",
            "Per-strategy rolling-window average fill rate evaluated by ExecutionQualityCircuitBreakerWorker.");
        ExecutionQualityCycleDurationMs = _meter.CreateHistogram<double>(
            "trading.execution_quality.cycle_duration_ms",
            "ms",
            "ExecutionQualityCircuitBreakerWorker cycle duration.");
        FeatureSchemaBackfillModelsSeen = _meter.CreateCounter<long>(
            "trading.feature_schema_backfill.models_seen",
            "models",
            "MLModel rows scanned by FeatureSchemaVersionBackfillWorker.");
        FeatureSchemaBackfillModelsUpdated = _meter.CreateCounter<long>(
            "trading.feature_schema_backfill.models_updated",
            "models",
            "Legacy MLModel snapshots successfully backfilled with an explicit FeatureSchemaVersion.");
        FeatureSchemaBackfillModelsUnresolved = _meter.CreateCounter<long>(
            "trading.feature_schema_backfill.models_unresolved",
            "models",
            "Legacy MLModel snapshots left unresolved by FeatureSchemaVersionBackfillWorker. Tagged with reason={deserialize_failed|empty_snapshot|insufficient_evidence|conflicting_evidence|unknown_feature_count}.");
        FeatureSchemaBackfillLockAttempts = _meter.CreateCounter<long>(
            "trading.feature_schema_backfill.lock_attempts",
            "cycles",
            "FeatureSchemaVersionBackfillWorker distributed-lock attempts. Tagged with outcome={acquired|busy|unavailable}.");
        FeatureSchemaBackfillCyclesSkipped = _meter.CreateCounter<long>(
            "trading.feature_schema_backfill.cycles_skipped",
            "cycles",
            "FeatureSchemaVersionBackfillWorker cycles skipped without processing. Tagged with reason={lock_busy|already_completed}.");
        FeatureSchemaBackfillCycleDurationMs = _meter.CreateHistogram<double>(
            "trading.feature_schema_backfill.cycle_duration_ms",
            "ms",
            "FeatureSchemaVersionBackfillWorker cycle duration.");
        FeatureStoreBackfillCandlesEvaluated = _meter.CreateCounter<long>(
            "trading.feature_store_backfill.candles_evaluated",
            "candles",
            "Historical candles evaluated for feature-store backfill under the current schema.");
        FeatureStoreBackfillVectorsWritten = _meter.CreateCounter<long>(
            "trading.feature_store_backfill.vectors_written",
            "vectors",
            "Historical feature vectors written or refreshed by FeatureStoreBackfillWorker.");
        FeatureStoreBackfillCandlesSkipped = _meter.CreateCounter<long>(
            "trading.feature_store_backfill.candles_skipped",
            "candles",
            "Historical candles skipped by FeatureStoreBackfillWorker. Tagged with reason={insufficient_history|compute_error}.");
        FeatureStoreBackfillLineageWrites = _meter.CreateCounter<long>(
            "trading.feature_store_backfill.lineage_writes",
            "writes",
            "FeatureVectorLineage records written by FeatureStoreBackfillWorker.");
        FeatureStoreBackfillLockAttempts = _meter.CreateCounter<long>(
            "trading.feature_store_backfill.lock_attempts",
            "cycles",
            "FeatureStoreBackfillWorker distributed-lock attempts. Tagged with outcome={acquired|busy|unavailable}.");
        FeatureStoreBackfillCyclesSkipped = _meter.CreateCounter<long>(
            "trading.feature_store_backfill.cycles_skipped",
            "cycles",
            "FeatureStoreBackfillWorker cycles skipped without processing. Tagged with reason=lock_busy.");
        FeatureStoreBackfillPendingCandles = _meter.CreateHistogram<double>(
            "trading.feature_store_backfill.pending_candles",
            "candles",
            "Historical stale or missing candles loaded into a FeatureStoreBackfillWorker cycle.");
        FeatureStoreBackfillCycleDurationMs = _meter.CreateHistogram<double>(
            "trading.feature_store_backfill.cycle_duration_ms",
            "ms",
            "FeatureStoreBackfillWorker cycle duration.");
        FeaturePrecomputePairsEvaluated = _meter.CreateCounter<long>(
            "trading.feature_precompute.pairs_evaluated",
            "pairs",
            "Active symbol/timeframe pairs evaluated by FeaturePreComputationWorker.");
        FeaturePrecomputeVectorsWritten = _meter.CreateCounter<long>(
            "trading.feature_precompute.vectors_written",
            "vectors",
            "Feature vectors written or refreshed by FeaturePreComputationWorker.");
        FeaturePrecomputePairsSkipped = _meter.CreateCounter<long>(
            "trading.feature_precompute.pairs_skipped",
            "pairs",
            "Active pairs skipped by FeaturePreComputationWorker. Tagged with reason={insufficient_history|already_fresh|pair_error}.");
        FeaturePrecomputeLineageWrites = _meter.CreateCounter<long>(
            "trading.feature_precompute.lineage_writes",
            "writes",
            "FeatureVectorLineage records written by FeaturePreComputationWorker.");
        FeaturePrecomputeLockAttempts = _meter.CreateCounter<long>(
            "trading.feature_precompute.lock_attempts",
            "cycles",
            "FeaturePreComputationWorker distributed-lock attempts. Tagged with outcome={acquired|busy|unavailable}.");
        FeaturePrecomputeCyclesSkipped = _meter.CreateCounter<long>(
            "trading.feature_precompute.cycles_skipped",
            "cycles",
            "FeaturePreComputationWorker cycles skipped without processing. Tagged with reason={lock_busy|no_active_pairs}.");
        FeaturePrecomputeCatchUpBars = _meter.CreateHistogram<double>(
            "trading.feature_precompute.catch_up_bars",
            "bars",
            "Number of recent bars refreshed for a pair in a FeaturePreComputationWorker cycle.");
        FeaturePrecomputePendingVectors = _meter.CreateHistogram<double>(
            "trading.feature_precompute.pending_vectors",
            "vectors",
            "Pending recent feature vectors detected in a FeaturePreComputationWorker cycle before writes.");
        FeaturePrecomputeCycleDurationMs = _meter.CreateHistogram<double>(
            "trading.feature_precompute.cycle_duration_ms",
            "ms",
            "FeaturePreComputationWorker cycle duration.");
        IntradayAttributionAccountsEvaluated = _meter.CreateCounter<long>(
            "trading.intraday_attribution.accounts_evaluated",
            "accounts",
            "Active accounts evaluated by IntradayAttributionWorker.");
        IntradayAttributionSnapshotsInserted = _meter.CreateCounter<long>(
            "trading.intraday_attribution.snapshots_inserted",
            "snapshots",
            "New hourly attribution snapshots inserted by IntradayAttributionWorker.");
        IntradayAttributionSnapshotsUpdated = _meter.CreateCounter<long>(
            "trading.intraday_attribution.snapshots_updated",
            "snapshots",
            "Existing hourly attribution snapshots refreshed by IntradayAttributionWorker.");
        IntradayAttributionLockAttempts = _meter.CreateCounter<long>(
            "trading.intraday_attribution.lock_attempts",
            "cycles",
            "IntradayAttributionWorker distributed-lock attempts. Tagged with outcome={acquired|busy|unavailable}.");
        IntradayAttributionCyclesSkipped = _meter.CreateCounter<long>(
            "trading.intraday_attribution.cycles_skipped",
            "cycles",
            "IntradayAttributionWorker cycles skipped without processing. Tagged with reason={disabled|lock_busy|no_active_accounts}.");
        IntradayAttributionCycleDurationMs = _meter.CreateHistogram<double>(
            "trading.intraday_attribution.cycle_duration_ms",
            "ms",
            "IntradayAttributionWorker cycle duration.");
        LatencySlaAlertTransitions = _meter.CreateCounter<long>(
            "trading.latency_sla.alert_transitions",
            "alerts",
            "LatencySlaWorker alert notifications and auto-resolutions. Tagged with segment and transition={dispatched|resolved}.");
        LatencySlaLockAttempts = _meter.CreateCounter<long>(
            "trading.latency_sla.lock_attempts",
            "cycles",
            "LatencySlaWorker distributed-lock attempts. Tagged with outcome={acquired|busy|unavailable}.");
        LatencySlaCyclesSkipped = _meter.CreateCounter<long>(
            "trading.latency_sla.cycles_skipped",
            "cycles",
            "LatencySlaWorker cycles skipped without processing. Tagged with reason={disabled|lock_busy}.");
        LatencySlaObservedP99Ms = _meter.CreateHistogram<double>(
            "trading.latency_sla.observed_p99_ms",
            "ms",
            "Observed rolling P99 latency per monitored SLA segment. Tagged with segment.");
        LatencySlaCycleDurationMs = _meter.CreateHistogram<double>(
            "trading.latency_sla.cycle_duration_ms",
            "ms",
            "LatencySlaWorker cycle duration.");

        // Candle Aggregation
        CandlesSynthesized = _meter.CreateCounter<long>(
            "trading.candles.synthesized",
            "candles",
            "Higher-timeframe candles synthesised from M1 by CandleAggregationWorker. Tagged with symbol and timeframe={H1|H4|D1}.");

        // Broker PnL Reconciliation
        BrokerReconciliationVariance = _meter.CreateHistogram<double>(
            "trading.reconciliation.broker_variance",
            "ratio",
            "Absolute fractional variance between engine-tracked and broker-reported account figures. Tagged with account_id, metric={equity|balance}, direction={over|under|exact} (engine_vs_broker).");
        BrokerReconciliationOutcomes = _meter.CreateCounter<long>(
            "trading.reconciliation.broker_outcomes",
            "outcomes",
            "Per-account reconciliation outcome counts. Tagged with metric={equity|balance} and outcome={ok|warning|critical|invalid|stale|currency_mismatch}.");
        BrokerReconciliationSnapshotAgeSeconds = _meter.CreateHistogram<double>(
            "trading.reconciliation.broker_snapshot_age",
            "s",
            "Age (seconds) of the broker account snapshot being reconciled against, at evaluation time. Tagged with account_id.");

        // Integration Event Retry
        EventRetrySuccesses    = _meter.CreateCounter<long>("trading.events.retry_successes", "events", "Integration events successfully re-published by retry worker");
        EventRetryExhausted    = _meter.CreateCounter<long>("trading.events.retry_exhausted", "events", "Integration events that exhausted retry attempts");
        EventRetryDeadLettered = _meter.CreateCounter<long>("trading.events.retry_dead_lettered", "events", "Integration events dead-lettered after retry exhaustion");
        EventRetryLockAttempts = _meter.CreateCounter<long>(
            "trading.events.retry_lock_attempts",
            "cycles",
            "IntegrationEventRetryWorker distributed-lock attempts. Tagged with outcome=acquired|busy|unavailable.");
        EventRetryCyclesSkipped = _meter.CreateCounter<long>(
            "trading.events.retry_cycles_skipped",
            "cycles",
            "IntegrationEventRetryWorker cycles skipped without processing. Tagged with reason=lock_busy|event_bus_degraded.");
        EventRetryBacklogDepth = _meter.CreateHistogram<double>(
            "trading.events.retry_backlog_depth",
            "events",
            "Retryable plus stale-published outbox rows loaded for an IntegrationEventRetryWorker cycle.");
        EventRetryCycleDurationMs = _meter.CreateHistogram<double>(
            "trading.events.retry_cycle_duration_ms",
            "ms",
            "IntegrationEventRetryWorker cycle duration.");

        // Degradation & Dead-Letter
        DegradationTransitions       = _meter.CreateCounter<long>("trading.degradation.transitions", "transitions", "Engine degradation mode transitions");
        DeadLetterSinkDbFailures     = _meter.CreateCounter<long>("trading.dead_letter.db_failures", "failures", "Dead-letter database write failures");
        DeadLetterSinkFileWrites     = _meter.CreateCounter<long>("trading.dead_letter.file_writes", "writes", "Dead-letter file fallback writes");
        DeadLetterSinkBufferOverflows = _meter.CreateCounter<long>("trading.dead_letter.buffer_overflows", "overflows", "Dead-letter emergency buffer overflow drops");
        DeadLetterSinkLatencyMs      = _meter.CreateHistogram<double>("trading.dead_letter.latency_ms", "ms", "Dead-letter sink write latency");

        // Strategy Generation
        StrategyCandidatesScreened = _meter.CreateCounter<long>("trading.strategy_generation.screened", "candidates", "Strategy candidates that completed screening backtest");
        StrategyCandidatesCreated  = _meter.CreateCounter<long>("trading.strategy_generation.created", "strategies", "Strategy candidates that passed screening and were created");
        StrategyCandidatesPruned   = _meter.CreateCounter<long>("trading.strategy_generation.pruned", "strategies", "Draft strategies pruned after repeated backtest failures");
        StrategyGenSymbolsSkipped  = _meter.CreateCounter<long>("trading.strategy_generation.symbols_skipped", "symbols", "Symbols skipped during strategy generation");
        StrategyGenRegimeConfidenceSkipped = _meter.CreateCounter<long>("trading.strategy_generation.regime_confidence_skipped", "symbols", "Symbols skipped due to low regime confidence");
        StrategyGenRegimeTransitionSkipped = _meter.CreateCounter<long>("trading.strategy_generation.regime_transition_skipped", "symbols", "Symbols skipped due to regime transition");
        StrategyGenDegradationRejected     = _meter.CreateCounter<long>("trading.strategy_generation.degradation_rejected", "candidates", "Candidates rejected due to excessive IS-to-OOS metric degradation");
        StrategyGenEquityCurveRejected     = _meter.CreateCounter<long>("trading.strategy_generation.equity_curve_rejected", "candidates", "Candidates rejected due to poor equity curve linearity (low R²)");
        StrategyGenTimeConcentrationRejected = _meter.CreateCounter<long>("trading.strategy_generation.time_concentration_rejected", "candidates", "Candidates rejected due to excessive trade time concentration");
        StrategyGenCorrelationSkipped      = _meter.CreateCounter<long>("trading.strategy_generation.correlation_skipped", "symbols", "Symbols skipped due to correlation group saturation");
        StrategyGenWalkForwardRejected     = _meter.CreateCounter<long>("trading.strategy_generation.walk_forward_rejected", "candidates", "Candidates rejected by walk-forward mini-validation");
        StrategyGenCircuitBreakerTripped   = _meter.CreateCounter<long>("trading.strategy_generation.circuit_breaker_tripped", "cycles", "Generation cycles skipped due to circuit breaker");
        StrategyGenFeedbackBoosted         = _meter.CreateCounter<long>("trading.strategy_generation.feedback_boosted", "candidates", "Candidates boosted or penalised by performance feedback");
        StrategyGenMonteCarloRejected      = _meter.CreateCounter<long>("trading.strategy_generation.monte_carlo_rejected", "candidates", "Candidates rejected by Monte Carlo permutation significance test");
        StrategyGenPortfolioDrawdownFiltered = _meter.CreateCounter<long>("trading.strategy_generation.portfolio_drawdown_filtered", "candidates", "Candidates removed by portfolio-level correlated drawdown check");
        StrategyGenPortfolioExposureFiltered = _meter.CreateCounter<long>("trading.strategy_generation.portfolio_exposure_filtered", "candidates", "Candidates removed by portfolio symbol/currency exposure capacity checks");
        StrategyGenAdaptiveThresholdsApplied = _meter.CreateCounter<long>("trading.strategy_generation.adaptive_thresholds_applied", "cycles", "Cycles where adaptive threshold calibration was applied");
        StrategyGenWeekendSkipped            = _meter.CreateCounter<long>("trading.strategy_generation.weekend_skipped", "cycles", "Cycles skipped because of weekend/holiday");
        StrategyGenOosDrawdownDegraded       = _meter.CreateCounter<long>("trading.strategy_generation.oos_drawdown_degraded", "candidates", "Candidates rejected for OOS drawdown degradation");
        StrategyGenReserveSpreadSkipped      = _meter.CreateCounter<long>("trading.strategy_generation.reserve_spread_skipped", "candidates", "Reserve candidates skipped due to spread/ATR filter");
        StrategyGenScreeningRejections       = _meter.CreateCounter<long>("trading.strategy_generation.screening_rejections", "candidates", "Candidates rejected by screening gates (tag: gate)");
        StrategyGenCandleCacheEvictions       = _meter.CreateCounter<long>("trading.strategy_generation.candle_cache_evictions", "evictions", "LRU candle cache evictions during strategy generation");
        StrategyGenFeedbackAdaptiveContradictions = _meter.CreateCounter<long>("trading.strategy_generation.feedback_adaptive_contradictions", "contradictions", "Strategy types where feedback boosts but adaptive thresholds tighten (tag: strategy_type)");
        StrategyGenTypeFaultDisabled         = _meter.CreateCounter<long>("trading.strategy_generation.type_fault_disabled", "types", "Strategy types disabled mid-cycle due to repeated screening faults (tag: strategy_type)");
        StrategyGenCompensationCleanupFailures = _meter.CreateCounter<long>("trading.strategy_generation.compensation_cleanup_failures", "failures", "Failures encountered while cleaning up partially persisted strategy-generation candidates");
        StrategyGenScreeningDurationMs       = _meter.CreateHistogram<double>("trading.strategy_generation.screening_duration", "ms", "Per-candidate screening pipeline duration (tag: strategy_type)");
        EvolutionaryCandidatesProposed       = _meter.CreateCounter<long>("trading.strategy_generation.evolutionary_proposed", "candidates", "Evolutionary offspring proposed before worker-side filtering.");
        EvolutionaryCandidatesInserted       = _meter.CreateCounter<long>("trading.strategy_generation.evolutionary_inserted", "candidates", "Evolutionary offspring persisted as draft strategies.");
        EvolutionaryCandidatesSkipped        = _meter.CreateCounter<long>("trading.strategy_generation.evolutionary_skipped", "candidates", "Evolutionary offspring skipped by the worker. Tagged with reason={parent_ineligible|invalid_parameters|duplicate_proposal|existing_strategy|active_validation_queue|persist_failed}.");
        EvolutionaryBacktestsQueued          = _meter.CreateCounter<long>("trading.strategy_generation.evolutionary_backtests_queued", "runs", "Initial validation backtests queued for persisted evolutionary offspring.");
        EvolutionaryCycleDurationMs          = _meter.CreateHistogram<double>("trading.strategy_generation.evolutionary_cycle_duration_ms", "ms", "EvolutionaryGeneratorWorker cycle duration.");

        // Optimization
        OptimizationRunsProcessed  = _meter.CreateCounter<long>("trading.optimization.runs_processed", "runs", "Optimization runs completed");
        OptimizationRunsFailed     = _meter.CreateCounter<long>("trading.optimization.runs_failed", "runs", "Optimization runs that failed with errors");
        OptimizationCandidatesScreened = _meter.CreateCounter<long>("trading.optimization.candidates_screened", "candidates", "Parameter candidates backtested during optimization");
        OptimizationAutoApproved   = _meter.CreateCounter<long>("trading.optimization.auto_approved", "runs", "Optimization runs auto-approved");
        OptimizationAutoRejected   = _meter.CreateCounter<long>("trading.optimization.auto_rejected", "runs", "Optimization runs sent to manual review");
        OptimizationCheckpointRestored = _meter.CreateCounter<long>("trading.optimization.checkpoint_restored", "runs", "Optimization runs resumed from checkpoint state");
        OptimizationCheckpointSaveFailures = _meter.CreateCounter<long>("trading.optimization.checkpoint_save_failures", "failures", "Checkpoint persistence failures during optimization");
        OptimizationLeaseReclaims = _meter.CreateCounter<long>("trading.optimization.lease_reclaims", "runs", "Stale running optimization runs reclaimed by lease recovery");
        OptimizationDuplicateFollowUpsPrevented = _meter.CreateCounter<long>("trading.optimization.duplicate_followups_prevented", "runs", "Duplicate validation follow-up creation attempts prevented");
        OptimizationRunsDeferred = _meter.CreateCounter<long>("trading.optimization.runs_deferred", "runs", "Optimization runs deferred back to queue (tagged by reason)");
        OptimizationFollowUpFailures = _meter.CreateCounter<long>("trading.optimization.followup_failures", "runs", "Validation follow-up backtests/walk-forwards that failed for an approved optimization");
        OptimizationFollowUpDeferredChecks = _meter.CreateCounter<long>("trading.optimization.followup_deferred_checks", "checks", "Follow-up monitor checks deferred because downstream validation is still in-flight");
        OptimizationFollowUpRepairs = _meter.CreateCounter<long>("trading.optimization.followup_repairs", "repairs", "Follow-up validation repairs triggered for approved optimizations");
        OptimizationFollowUpQueueAgeMs = _meter.CreateHistogram<double>("trading.optimization.followup_queue_age_ms", "ms", "Age of an approved optimization when the follow-up monitor evaluates it");
        OptimizationApprovalToFollowUpCreationMs = _meter.CreateHistogram<double>("trading.optimization.approval_to_followup_creation_ms", "ms", "Delay between optimization approval and validation follow-up creation");
        OptimizationCompletionPublicationLagMs = _meter.CreateHistogram<double>("trading.optimization.completion_publication_lag_ms", "ms", "Delay between terminal optimization state and successful completion publication");
        OptimizationCompletionReplayWaitMs = _meter.CreateHistogram<double>("trading.optimization.completion_replay_wait_ms", "ms", "How long replay callers waited for detached completion publication work");
        OptimizationCompletionReplayAttempts = _meter.CreateCounter<long>("trading.optimization.completion_replay_attempts", "attempts", "Completion publication replay attempts");
        OptimizationCompletionReplayFailures = _meter.CreateCounter<long>("trading.optimization.completion_replay_failures", "failures", "Completion publication replay failures");
        OptimizationSurrogateImprovement = _meter.CreateHistogram<double>("trading.optimization.surrogate_improvement", "score", "Per-batch best score improvement over previous best during surrogate search");
        OptimizationSurrogateBatchHits = _meter.CreateCounter<long>("trading.optimization.surrogate_batch_hits", "batches", "Surrogate-guided batches that improved the global best score");
        OptimizationSurrogateBatchMisses = _meter.CreateCounter<long>("trading.optimization.surrogate_batch_misses", "batches", "Surrogate-guided batches that did not improve the global best score");
        OptimizationEarlyStopSavings = _meter.CreateCounter<long>("trading.optimization.early_stop_savings", "evaluations", "Evaluations saved by early stopping in surrogate search");
        OptimizationParameterSpaceExhausted = _meter.CreateCounter<long>("trading.optimization.parameter_space_exhausted", "runs", "Optimization runs that found no fresh parameter candidates after grid expansion");
        OptimizationCircuitBreakerTrips = _meter.CreateCounter<long>("trading.optimization.circuit_breaker_trips", "trips", "Times the backtest circuit breaker tripped due to consecutive failures");
        OptimizationGateRejections = _meter.CreateCounter<long>("trading.optimization.gate_rejections", "rejections", "Validation gate rejections during optimization (tagged by gate name)");
        OptimizationGateDurationMs = _meter.CreateHistogram<double>("trading.optimization.gate_duration_ms", "ms", "Duration of individual validation gates (tagged by gate name)");
        OptimizationComputeSeconds = _meter.CreateHistogram<double>("trading.optimization.compute_seconds", "seconds", "Total backtest compute time per optimization run");
        OptimizationPhaseDurationMs = _meter.CreateHistogram<double>("trading.optimization.phase_duration_ms", "ms", "Duration of individual optimization phases (tagged by phase)");
        OptimizationCycleDurationMs = _meter.CreateHistogram<double>("trading.optimization.cycle_duration_ms", "ms", "Duration of a single optimization run");
        OptimizationClaimLatencyMs = _meter.CreateHistogram<double>("trading.optimization.claim_latency_ms", "ms", "Latency of the atomic queued-run claim operation");
        OptimizationQueueWaitAtClaimMs = _meter.CreateHistogram<double>("trading.optimization.queue_wait_at_claim_ms", "ms", "How long a run waited in the queue before being claimed");
        OptimizationActiveProcessingSlots = _meter.CreateHistogram<double>("trading.optimization.active_processing_slots", "slots", "Active optimization processing slots observed by the coordinator");
        OptimizationProcessingSlotUtilization = _meter.CreateHistogram<double>("trading.optimization.processing_slot_utilization", "ratio", "Observed ratio of active optimization slots to configured max concurrency");
        EhviHypervolumeProgress = _meter.CreateHistogram<double>("trading.optimization.ehvi_hypervolume", "volume", "Pareto front hypervolume after each EHVI batch (tracks multi-objective search progress)");
        EhviGpPredictionError = _meter.CreateHistogram<double>("trading.optimization.ehvi_gp_prediction_error", "error", "Per-objective GP mean absolute prediction error per batch");
        OptimizationSurrogateClamps = _meter.CreateCounter<long>("trading.optimization.surrogate_clamps", "clamps", "GP Cholesky diagonal clamps indicating ill-conditioned surrogate");
        OptimizationDeferredRechecks = _meter.CreateCounter<long>("trading.optimization.deferred_rechecks", "rechecks", "Times a deferred run was rechecked before its condition cleared");
        HyperbandBracketsExecuted = _meter.CreateCounter<long>("trading.optimization.hyperband_brackets_executed", "brackets", "Hyperband brackets that completed successfully");
        HyperbandBracketsSkipped = _meter.CreateCounter<long>("trading.optimization.hyperband_brackets_skipped", "brackets", "Hyperband brackets skipped (budget exhausted or failure)");
        HyperbandSurvivalRate = _meter.CreateHistogram<double>("trading.optimization.hyperband_survival_rate", "ratio", "Fraction of candidates surviving each Hyperband bracket (0-1)");

        // Candle Aggregator
        CandlesClosed      = _meter.CreateCounter<long>("trading.candles.closed", "candles", "Candle bars closed by aggregator");
        CandlesSynthetic   = _meter.CreateCounter<long>("trading.candles.synthetic", "candles", "Synthetic gap-fill candles emitted");
        CandleTicksDropped = _meter.CreateCounter<long>("trading.candles.ticks_dropped", "ticks", "Out-of-order ticks dropped by aggregator");
        CandleSlotsPurged  = _meter.CreateCounter<long>("trading.candles.slots_purged", "slots", "Stale candle slots purged");
        CandleTicksSpreadRejected = _meter.CreateCounter<long>("trading.candles.ticks_spread_rejected", "ticks", "Ticks rejected due to excessive spread");

        // News Sentiment
        NewsSentimentCallsTotal = _meter.CreateCounter<long>("trading.news_sentiment.calls_total", "calls", "Total DeepSeek API calls for news sentiment");
        NewsSentimentCallErrors = _meter.CreateCounter<long>("trading.news_sentiment.call_errors", "errors", "DeepSeek API call failures");
        NewsSentimentHeadlinesProcessed = _meter.CreateCounter<long>("trading.news_sentiment.headlines_processed", "headlines", "Headlines analyzed by DeepSeek");
        NewsSentimentEventsProcessed = _meter.CreateCounter<long>("trading.news_sentiment.events_processed", "events", "Economic events analyzed by DeepSeek");
        NewsSentimentCycleDurationMs = _meter.CreateHistogram<double>("trading.news_sentiment.cycle_duration_ms", "ms", "NewsSentimentWorker cycle duration");
        DeepSeekApiLatencyMs = _meter.CreateHistogram<double>("trading.news_sentiment.deepseek_latency_ms", "ms", "DeepSeek API call latency");
        DeepSeekTokensUsed = _meter.CreateCounter<long>("trading.news_sentiment.deepseek_tokens_used", "tokens", "Total tokens consumed by DeepSeek API");

        // Economic Calendar
        EconEventsIngested  = _meter.CreateCounter<long>("trading.econ_calendar.events_ingested", "events", "Economic events ingested from feed");
        EconActualsPatched  = _meter.CreateCounter<long>("trading.econ_calendar.actuals_patched", "events", "Economic events patched with actual values");
        EconFeedErrors      = _meter.CreateCounter<long>("trading.econ_calendar.feed_errors", "errors", "Economic calendar feed errors");
        EconCircuitBreakerSkips = _meter.CreateCounter<long>("trading.econ_calendar.circuit_breaker_skips", "cycles", "Economic calendar ingestion cycles skipped because the feed circuit breaker is open.");
        EconAlertTransitions = _meter.CreateCounter<long>("trading.econ_calendar.alert_transitions", "alerts", "Economic calendar feed-health alert notifications and resolutions. Tagged with transition={dispatched|resolved}.");
        EconCycleDurationMs = _meter.CreateHistogram<double>("trading.econ_calendar.cycle_duration", "ms", "Economic calendar worker full cycle duration");
        EconPendingActualsBacklog = _meter.CreateHistogram<double>("trading.econ_calendar.pending_actuals_backlog", "events", "Pending economic events awaiting actual-value patching in a worker cycle.");

        // Economic Calendar Feed
        EconFeedFetches       = _meter.CreateCounter<long>("trading.econ_calendar.feed_fetches", "fetches", "HTTP fetches to calendar feed");
        EconFeedCacheHits     = _meter.CreateCounter<long>("trading.econ_calendar.feed_cache_hits", "hits", "Calendar feed cache hits (HTML or parsed)");
        EconFeedParseFailures = _meter.CreateCounter<long>("trading.econ_calendar.feed_parse_failures", "failures", "Calendar pages fetched but parsed zero events");
        EconFeedFetchLatencyMs = _meter.CreateHistogram<double>("trading.econ_calendar.feed_fetch_latency", "ms", "Calendar feed HTTP fetch latency");

        // Tick Ingestion
        TicksIngested          = _meter.CreateCounter<long>("trading.ticks.ingested", "ticks", "Total ticks received from EA instances");
        TickBatchSize          = _meter.CreateHistogram<double>("trading.ticks.batch_size", "ticks", "Number of ticks per EA batch submission");
        TickIngestionLatencyMs = _meter.CreateHistogram<double>("trading.ticks.ingestion_latency", "ms", "Tick batch processing latency");

        // Cache
        PriceCacheMisses = _meter.CreateCounter<long>("trading.cache.price_misses", "misses", "Live price cache misses");

        // Simulated Broker
        SimBrokerFills         = _meter.CreateCounter<long>("trading.sim_broker.fills", "fills", "Simulated broker order fills");
        SimBrokerRejects       = _meter.CreateCounter<long>("trading.sim_broker.rejects", "rejects", "Simulated broker order rejections");
        SimBrokerRequotes      = _meter.CreateCounter<long>("trading.sim_broker.requotes", "requotes", "Simulated broker requotes issued");
        SimBrokerDisconnects   = _meter.CreateCounter<long>("trading.sim_broker.disconnects", "disconnects", "Simulated broker connection failures");
        SimBrokerStopOuts      = _meter.CreateCounter<long>("trading.sim_broker.stop_outs", "stop_outs", "Simulated broker stop-out liquidations");
        SimBrokerMarginCalls   = _meter.CreateCounter<long>("trading.sim_broker.margin_calls", "margin_calls", "Simulated broker margin call warnings");
        SimBrokerSlTpCloses    = _meter.CreateCounter<long>("trading.sim_broker.sl_tp_closes", "closes", "Simulated broker SL/TP position closes");
        SimBrokerPendingFills  = _meter.CreateCounter<long>("trading.sim_broker.pending_fills", "fills", "Simulated broker pending order triggers");
        SimBrokerExpiredOrders = _meter.CreateCounter<long>("trading.sim_broker.expired_orders", "orders", "Simulated broker expired pending orders");
        SimBrokerMarginLevel   = _meter.CreateHistogram<double>("trading.sim_broker.margin_level", "%", "Simulated broker margin level at evaluation");
        SimBrokerRealizedPnl   = _meter.CreateHistogram<double>("trading.sim_broker.realized_pnl", "USD", "Simulated broker realized P&L per close");

        // Observable gauges — sampled on scrape, not pushed
        PriceCacheSymbolCount  = _meter.CreateObservableGauge("trading.cache.symbol_count", () => _priceCacheSymbolCountFunc?.Invoke() ?? 0, "symbols", "Number of symbols in live price cache");
        PriceCacheEvictions    = _meter.CreateCounter<long>("trading.cache.evictions", "evictions", "Price cache stale evictions");
        PriceFeedStaleAlert    = _meter.CreateCounter<long>("trading.cache.feed_stale_alert", "alerts", "Multi-symbol stale price feed alerts");
    }

    // Observable gauge callback registration
    private Func<int>? _priceCacheSymbolCountFunc;
    public void RegisterPriceCacheGauge(Func<int> symbolCountFunc) => _priceCacheSymbolCountFunc = symbolCountFunc;
    public ObservableGauge<int> PriceCacheSymbolCount { get; }
    public Counter<long> PriceCacheEvictions { get; }
    public Counter<long> PriceFeedStaleAlert { get; }
}
