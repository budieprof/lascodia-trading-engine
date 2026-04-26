using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLAdwinDriftWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_DegradingShift_SetsFutureFlag_QueuesRetraining_AndWritesDriftLog()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var flag = await harness.LoadFlagAsync("EURUSD", Timeframe.H1);
        var run = Assert.Single(await harness.LoadTrainingRunsAsync());
        var driftLog = Assert.Single(await harness.LoadDriftLogsAsync());

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.CandidateModelCount);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(1, result.DriftCount);
        Assert.Equal(1, result.RetrainingQueuedCount);
        Assert.Equal(0, result.FlagClearCount);

        Assert.NotNull(flag);
        Assert.False(flag!.IsDeleted);
        Assert.True(flag.ExpiresAtUtc > now.UtcDateTime);
        Assert.Equal("AdwinDrift", flag.DetectorType);
        Assert.Equal(1, flag.ConsecutiveDetections);

        Assert.Equal(TriggerType.AutoDegrading, run.TriggerType);
        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal(LearnerArchitecture.BaggedLogistic, run.LearnerArchitecture);
        Assert.Equal("AdwinDrift", run.DriftTriggerType);
        Assert.Equal(1, run.Priority);

        using var metadata = JsonDocument.Parse(run.DriftMetadataJson!);
        Assert.Equal("ADWIN", metadata.RootElement.GetProperty("detector").GetString());
        Assert.Equal("degradation", metadata.RootElement.GetProperty("direction").GetString());
        Assert.True(metadata.RootElement.GetProperty("accuracyDrop").GetDouble() > 0);

        Assert.True(driftLog.DriftDetected);
        Assert.True(driftLog.Window1Mean > driftLog.Window2Mean);
        Assert.True(driftLog.EpsilonCut > 0);
        Assert.True(driftLog.AccuracyDrop > 0);
        Assert.Equal(0.002, driftLog.DeltaUsed, 6);
        Assert.Equal(100, driftLog.Window1Size + driftLog.Window2Size);
        Assert.NotNull(driftLog.OutcomeSeriesCompressed);
        Assert.Equal(100, DecompressOutcomeSeries(driftLog.OutcomeSeriesCompressed!).Length);
    }

    [Fact]
    public async Task RunCycleAsync_SignificantImprovement_DoesNotSetFlagOrQueueRetraining()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                // Pre-existing live drift flag — we expect the worker to expire it.
                db.Set<MLDriftFlag>().Add(new MLDriftFlag
                {
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    DetectorType = "AdwinDrift",
                    ExpiresAtUtc = now.AddHours(4).UtcDateTime,
                    FirstDetectedAtUtc = now.AddDays(-1).UtcDateTime,
                    LastRefreshedAtUtc = now.AddHours(-1).UtcDateTime,
                    ConsecutiveDetections = 3,
                    IsDeleted = false,
                });

                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(false, 60).Concat(Enumerable.Repeat(true, 40))));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var flag = await harness.LoadFlagAsync("EURUSD", Timeframe.H1);
        var driftLog = Assert.Single(await harness.LoadDriftLogsAsync());

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(0, result.DriftCount);
        Assert.Equal(0, result.RetrainingQueuedCount);
        Assert.Equal(1, result.FlagClearCount);
        Assert.Empty(await harness.LoadTrainingRunsAsync());

        Assert.NotNull(flag);
        Assert.True(flag!.ExpiresAtUtc <= now.UtcDateTime);
        Assert.Equal(0, flag.ConsecutiveDetections);

        Assert.False(driftLog.DriftDetected);
        Assert.True(driftLog.Window2Mean > driftLog.Window1Mean);
        Assert.Equal(0.0, driftLog.AccuracyDrop);
    }

    [Fact]
    public async Task RunCycleAsync_StaleResolvedHistoryOutsideLookback_IsSkipped()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                AddConfig(db, "MLAdwinDrift:LookbackDays", "1");

                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddDays(-10).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        // The "has >= MinResolvedPredictions" filter now lives in SQL, so dormant models
        // never reach the per-model evaluation loop and the candidate count is zero.
        Assert.Equal(0, result.CandidateModelCount);
        Assert.Equal(0, result.EvaluatedModelCount);
        Assert.Equal(0, result.DriftCount);
        Assert.Empty(await harness.LoadDriftLogsAsync());
        Assert.Empty(await harness.LoadTrainingRunsAsync());
        Assert.Null(await harness.LoadFlagAsync("EURUSD", Timeframe.H1));
    }

    [Fact]
    public async Task RunCycleAsync_RevivesSoftDeletedFlag_WhenDriftDetected()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLDriftFlag>().Add(new MLDriftFlag
                {
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    DetectorType = "AdwinDrift",
                    ExpiresAtUtc = now.AddHours(-2).UtcDateTime,
                    FirstDetectedAtUtc = now.AddDays(-2).UtcDateTime,
                    LastRefreshedAtUtc = now.AddDays(-1).UtcDateTime,
                    ConsecutiveDetections = 0,
                    IsDeleted = true,
                });

                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var revivedFlag = await harness.LoadFlagAsync("EURUSD", Timeframe.H1, includeDeleted: true);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.DriftCount);
        Assert.NotNull(revivedFlag);
        Assert.False(revivedFlag!.IsDeleted);
        Assert.True(revivedFlag.ExpiresAtUtc > now.UtcDateTime);
        Assert.Equal(1, revivedFlag.ConsecutiveDetections);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingState()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now),
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await harness.LoadDriftLogsAsync());
        Assert.Empty(await harness.LoadTrainingRunsAsync());
        Assert.Null(await harness.LoadFlagAsync("EURUSD", Timeframe.H1));
    }

    [Fact]
    public async Task RunCycleAsync_InvalidSettingsAreClampedSafely()
    {
        using var harness = CreateHarness(db =>
        {
            AddConfig(db, "MLAdwinDrift:PollIntervalSeconds", "-1");
            AddConfig(db, "MLAdwinDrift:WindowSize", "1");
            AddConfig(db, "MLAdwinDrift:MinResolvedPredictions", "999999");
            AddConfig(db, "MLAdwinDrift:Delta", "0");
            AddConfig(db, "MLAdwinDrift:LookbackDays", "0");
            AddConfig(db, "MLAdwinDrift:FlagTtlHours", "0");
            AddConfig(db, "MLAdwinDrift:MaxModelsPerCycle", "0");
            AddConfig(db, "MLAdwinDrift:LockTimeoutSeconds", "-2");
            AddConfig(db, "MLAdwinDrift:MinTimeBetweenRetrainsHours", "-7");
            AddConfig(db, "MLAdwinDrift:DbCommandTimeoutSeconds", "-1");
            AddConfig(db, "MLTraining:TrainingDataWindowDays", "0");
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(24 * 60 * 60), result.Settings.PollInterval);
        Assert.Equal(60, result.Settings.WindowSize);
        Assert.Equal(60, result.Settings.MinResolvedPredictions);
        Assert.Equal(0.002, result.Settings.Delta, 6);
        Assert.Equal(180, result.Settings.LookbackDays);
        Assert.Equal(48, result.Settings.FlagTtlHours);
        Assert.Equal(256, result.Settings.MaxModelsPerCycle);
        Assert.Equal(5, result.Settings.LockTimeoutSeconds);
        Assert.Equal(365, result.Settings.TrainingDataWindowDays);
        Assert.Equal(12, result.Settings.MinTimeBetweenRetrainsHours);
        Assert.Equal(60, result.Settings.DbCommandTimeoutSeconds);
        Assert.True(result.Settings.SnapshotOutcomeSeries);
    }

    [Fact]
    public async Task RunCycleAsync_UsesStrongestDegradingSplit_NotFirstSignificantSplit()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 60).Concat(Enumerable.Repeat(false, 40))));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var driftLog = Assert.Single(await harness.LoadDriftLogsAsync());

        Assert.Equal(1, result.DriftCount);
        Assert.True(driftLog.DriftDetected);
        Assert.Equal(60, driftLog.Window1Size);
        Assert.Equal(40, driftLog.Window2Size);
        Assert.True(driftLog.Window1Mean > driftLog.Window2Mean);
    }

    [Fact]
    public async Task RunCycleAsync_RecentCompletedAutoDegradingRun_SuppressesNewQueueing()
    {
        // Simulates the cooldown path: an earlier auto-degrading run completed 6 hours
        // ago and is still propagating through SPRT shadow evaluation. A fresh degradation
        // signal should NOT queue a new run inside the 12h cooldown window.
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    TriggerType = TriggerType.AutoDegrading,
                    Status = RunStatus.Completed,
                    DriftTriggerType = "AdwinDrift",
                    StartedAt = now.AddHours(-7).UtcDateTime,
                    CompletedAt = now.AddHours(-6).UtcDateTime,
                    FromDate = now.AddDays(-365).UtcDateTime,
                    ToDate = now.AddHours(-7).UtcDateTime,
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    IsDeleted = false,
                });

                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.DriftCount);
        Assert.Equal(0, result.RetrainingQueuedCount);
        // Only the seeded historical run should remain; no new auto-degrading run.
        var runs = await harness.LoadTrainingRunsAsync();
        Assert.Single(runs);
        Assert.Equal(RunStatus.Completed, runs[0].Status);
    }

    [Fact]
    public async Task RunCycleAsync_OldCompletedRun_OutsideCooldown_DoesQueueRetraining()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    TriggerType = TriggerType.AutoDegrading,
                    Status = RunStatus.Completed,
                    DriftTriggerType = "AdwinDrift",
                    StartedAt = now.AddDays(-3).UtcDateTime,
                    CompletedAt = now.AddDays(-2).UtcDateTime,
                    FromDate = now.AddDays(-365).UtcDateTime,
                    ToDate = now.AddDays(-3).UtcDateTime,
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    IsDeleted = false,
                });

                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.DriftCount);
        Assert.Equal(1, result.RetrainingQueuedCount);
    }

    [Fact]
    public async Task RunCycleAsync_PerPairDeltaOverride_AppliedAndRecordedInAuditRow()
    {
        // The override should reach AdwinDetector and be persisted on the audit row.
        // We verify wiring (DeltaUsed) rather than detection outcome, because the
        // detection outcome depends on the interaction between delta and the outcome
        // stream and is covered by AdwinDetectorTest.
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                AddConfig(db, "MLAdwinDrift:Override:EURUSD:H1:Delta", "0.05");

                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now));

        await harness.Worker.RunCycleAsync(CancellationToken.None);
        var driftLog = Assert.Single(await harness.LoadDriftLogsAsync());

        Assert.Equal(0.05, driftLog.DeltaUsed, 6);
    }

    [Fact]
    public async Task RunCycleAsync_PerPairWindowSizeOverride_ReducesObservationWindow()
    {
        // Override the window size and confirm the audit row's window sums to it.
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                AddConfig(db, "MLAdwinDrift:Override:EURUSD:H1:WindowSize", "80");

                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-200).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 100).Concat(Enumerable.Repeat(false, 100))));
            },
            timeProvider: new TestTimeProvider(now));

        await harness.Worker.RunCycleAsync(CancellationToken.None);
        var driftLog = Assert.Single(await harness.LoadDriftLogsAsync());

        Assert.Equal(80, driftLog.Window1Size + driftLog.Window2Size);
    }

    [Fact]
    public async Task RunCycleAsync_DominantRegimeRecorded_FromMostRecentSnapshot()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
                {
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    Regime = MarketRegime.HighVolatility,
                    Confidence = 0.85m,
                    DetectedAt = now.AddHours(-2).UtcDateTime,
                    IsDeleted = false,
                });

                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now));

        await harness.Worker.RunCycleAsync(CancellationToken.None);
        var driftLog = Assert.Single(await harness.LoadDriftLogsAsync());

        Assert.Equal(MarketRegime.HighVolatility, driftLog.DominantRegime);
    }

    [Fact]
    public async Task RunCycleAsync_DispatchesAlert_OnDrift()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var dispatcher = new RecordingAlertDispatcher();

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now),
            alertDispatcher: dispatcher);

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var dispatched = Assert.Single(dispatcher.Dispatched);
        Assert.Equal(AlertType.MLModelDegraded, dispatched.alert.AlertType);
        Assert.Equal(AlertSeverity.Medium, dispatched.alert.Severity);
        Assert.Equal("EURUSD", dispatched.alert.Symbol);
        Assert.Equal("adwin-drift:EURUSD:H1:AdwinDrift", dispatched.alert.DeduplicationKey);
        Assert.True(dispatched.alert.CooldownSeconds > 0);
        Assert.Contains("ADWIN drift", dispatched.message);
    }

    [Fact]
    public async Task RunCycleAsync_FleetSystemicAlert_FiresWhenManyModelsDriftSimultaneously()
    {
        // Three pairs all drift in one cycle. With FleetSystemicDriftThreshold=2, the
        // worker raises a single SystemicMLDegradation alert keyed by the fleet dedupe.
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                long modelId = 1;
                long logIdOffset = 0;
                var startUtc = now.AddHours(-100).UtcDateTime;
                foreach (var symbol in new[] { "EURUSD", "GBPUSD", "USDJPY" })
                {
                    db.Set<MLModel>().Add(new MLModel
                    {
                        Id = modelId,
                        Symbol = symbol,
                        Timeframe = Timeframe.H1,
                        ModelVersion = "1.0.0",
                        FilePath = "/tmp/model.bin",
                        Status = MLModelStatus.Active,
                        IsActive = true,
                        TrainedAt = new DateTime(2026, 04, 20, 12, 0, 0, DateTimeKind.Utc),
                        ModelBytes = [1, 2, 3],
                        LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                        IsDeleted = false,
                        RowVersion = 1
                    });
                    db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                        modelId: modelId,
                        startUtc: startUtc,
                        outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50)),
                        idOffset: logIdOffset,
                        symbol: symbol));
                    logIdOffset += 100;
                    modelId++;
                }
            },
            timeProvider: new TestTimeProvider(now),
            options: new LascodiaTradingEngine.Application.Common.Options.MLAdwinDriftOptions
            {
                FleetSystemicDriftThreshold = 2,
            });

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var alert = await harness.LoadAlertAsync(MLAdwinDriftWorker.FleetSystemicDedupeKey);
        Assert.NotNull(alert);
        Assert.Equal(AlertType.SystemicMLDegradation, alert!.AlertType);
        Assert.True(alert.IsActive);
        Assert.Contains("fleet_systemic_drift", alert.ConditionJson);
    }

    [Fact]
    public async Task RunCycleAsync_StalenessAlert_FiresWhenNewestDriftLogIsOld()
    {
        // Pre-seed an MLAdwinDriftLog whose DetectedAt is older than StalenessAlertHours.
        // The cycle has no candidates (no models seeded), so no fresh log is written.
        // Staleness phase still runs and fires the alert.
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                db.Set<MLAdwinDriftLog>().Add(new MLAdwinDriftLog
                {
                    MLModelId = 99,
                    Symbol = "ZZZUSD",
                    Timeframe = Timeframe.H1,
                    DriftDetected = false,
                    Window1Mean = 0.5,
                    Window2Mean = 0.5,
                    EpsilonCut = 0.05,
                    Window1Size = 50,
                    Window2Size = 50,
                    DetectedAt = now.AddHours(-100).UtcDateTime, // 100h old
                    AccuracyDrop = 0.0,
                    DeltaUsed = 0.002,
                    IsDeleted = false,
                });
            },
            timeProvider: new TestTimeProvider(now),
            options: new LascodiaTradingEngine.Application.Common.Options.MLAdwinDriftOptions
            {
                StalenessAlertHours = 36,
            });

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var alert = await harness.LoadAlertAsync(MLAdwinDriftWorker.StalenessDedupeKey);
        Assert.NotNull(alert);
        Assert.Equal(AlertType.DataQualityIssue, alert!.AlertType);
        Assert.True(alert.IsActive);
        Assert.Contains("adwin_drift_log_stale", alert.ConditionJson);
    }

    [Fact]
    public async Task RunCycleAsync_OverrideHierarchy_SymbolOnlyTier_AppliesToMatchingPair()
    {
        // Symbol-only tier override (no Timeframe qualifier) should apply when no more-
        // specific override exists. Tightens delta from 0.002 to 0.05 so a borderline
        // distribution that would not normally trip becomes a drift.
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                AddConfig(db, "MLAdwinDrift:Override:Symbol:EURUSD:Delta", "0.05");
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now));

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var driftLogs = await harness.LoadDriftLogsAsync();
        var log = Assert.Single(driftLogs);
        Assert.Equal(0.05, log.DeltaUsed, precision: 6);
    }

    [Fact]
    public async Task RunCycleAsync_OverrideHierarchy_FirstHitWins_MoreSpecificTakesPrecedence()
    {
        // Symbol-only tier sets Delta=0.05 (would apply if alone).
        // Symbol+Timeframe explicit tier sets Delta=0.10 (more specific, wins).
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                AddConfig(db, "MLAdwinDrift:Override:Symbol:EURUSD:Delta", "0.05");
                AddConfig(db, "MLAdwinDrift:Override:Symbol:EURUSD:Timeframe:H1:Delta", "0.10");
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now));

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var driftLogs = await harness.LoadDriftLogsAsync();
        var log = Assert.Single(driftLogs);
        Assert.Equal(0.10, log.DeltaUsed, precision: 6);
    }

    [Theory]
    [InlineData(0,    1, 1, 1)]    // jitter disabled → returns base unchanged
    [InlineData(60,   1, 1, 61)]   // base 1s + uniform[0, 60] ∈ [1, 61]
    [InlineData(600, 60, 60, 660)] // base 60s + uniform[0, 600] ∈ [60, 660]
    public void ApplyJitter_RespectsBoundsAndDisableSemantics(int jitterSeconds, int baseSeconds, int minTotal, int maxTotal)
    {
        var result = MLAdwinDriftWorker.ApplyJitter(TimeSpan.FromSeconds(baseSeconds), jitterSeconds);
        Assert.InRange(result.TotalSeconds, minTotal, maxTotal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86_401)]
    public void Validator_RejectsOutOfRangePollJitter(int value)
    {
        var validator = new LascodiaTradingEngine.Application.Common.Options.MLAdwinDriftOptionsValidator();
        var result = validator.Validate(name: null,
            new LascodiaTradingEngine.Application.Common.Options.MLAdwinDriftOptions { PollJitterSeconds = value });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("PollJitterSeconds"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(17)]
    public void Validator_RejectsOutOfRangeBackoffShift(int value)
    {
        var validator = new LascodiaTradingEngine.Application.Common.Options.MLAdwinDriftOptionsValidator();
        var result = validator.Validate(name: null,
            new LascodiaTradingEngine.Application.Common.Options.MLAdwinDriftOptions { FailureBackoffCapShift = value });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("FailureBackoffCapShift"));
    }

    [Fact]
    public void Validator_RejectsZeroFleetSystemicDriftThreshold()
    {
        var validator = new LascodiaTradingEngine.Application.Common.Options.MLAdwinDriftOptionsValidator();
        var result = validator.Validate(name: null,
            new LascodiaTradingEngine.Application.Common.Options.MLAdwinDriftOptions { FleetSystemicDriftThreshold = 0 });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("FleetSystemicDriftThreshold"));
    }

    private static byte[] DecompressOutcomeSeries(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static WorkerHarness CreateHarness(
        Action<MLAdwinDriftWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null,
        IAlertDispatcher? alertDispatcher = null,
        LascodiaTradingEngine.Application.Common.Options.MLAdwinDriftOptions? options = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLAdwinDriftWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<MLAdwinDriftWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(provider => provider.GetRequiredService<MLAdwinDriftWorkerTestContext>());

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLAdwinDriftWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLAdwinDriftWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLAdwinDriftWorker>.Instance,
            distributedLock: distributedLock,
            healthMonitor: null,
            metrics: null,
            timeProvider: timeProvider,
            alertDispatcher: alertDispatcher,
            dbExceptionClassifier: null,
            options: options);

        return new WorkerHarness(provider, connection, worker);
    }

    private static void SeedActiveModel(
        MLAdwinDriftWorkerTestContext db,
        long id)
    {
        db.Set<MLModel>().Add(new MLModel
        {
            Id = id,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = "1.0.0",
            FilePath = "/tmp/model.bin",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainedAt = new DateTime(2026, 04, 20, 12, 0, 0, DateTimeKind.Utc),
            ModelBytes = [1, 2, 3],
            LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            IsDeleted = false,
            RowVersion = 1
        });
    }

    private static void AddConfig(
        MLAdwinDriftWorkerTestContext db,
        string key,
        string value)
    {
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = ConfigDataType.String,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow,
            IsDeleted = false
        });
    }

    private static IReadOnlyList<MLModelPredictionLog> NewLogs(
        long modelId,
        DateTime startUtc,
        IEnumerable<bool> outcomes,
        long idOffset = 0,
        string symbol = "EURUSD")
    {
        var logs = new List<MLModelPredictionLog>();
        int index = 0;

        foreach (var directionCorrect in outcomes)
        {
            var predictedAtUtc = startUtc.AddHours(index);
            logs.Add(new MLModelPredictionLog
            {
                Id = idOffset + index + 1,
                TradeSignalId = idOffset + index + 1,
                MLModelId = modelId,
                ModelRole = ModelRole.Champion,
                Symbol = symbol,
                Timeframe = Timeframe.H1,
                PredictedDirection = TradeDirection.Buy,
                PredictedMagnitudePips = 0,
                ConfidenceScore = 0.75m,
                ServedCalibratedProbability = 0.75m,
                DecisionThresholdUsed = 0.50m,
                ActualDirection = directionCorrect ? TradeDirection.Buy : TradeDirection.Sell,
                ActualMagnitudePips = directionCorrect ? 10m : -10m,
                DirectionCorrect = directionCorrect,
                PredictedAt = predictedAtUtc,
                OutcomeRecordedAt = predictedAtUtc.AddMinutes(5),
                IsDeleted = false
            });
            index++;
        }

        return logs;
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MLAdwinDriftWorker worker) : IDisposable
    {
        public MLAdwinDriftWorker Worker { get; } = worker;

        public async Task<List<MLTrainingRun>> LoadTrainingRunsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAdwinDriftWorkerTestContext>();
            return await db.Set<MLTrainingRun>()
                .AsNoTracking()
                .OrderBy(run => run.Id)
                .ToListAsync();
        }

        public async Task<List<MLAdwinDriftLog>> LoadDriftLogsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAdwinDriftWorkerTestContext>();
            return await db.Set<MLAdwinDriftLog>()
                .AsNoTracking()
                .OrderBy(log => log.Id)
                .ToListAsync();
        }

        public async Task<MLDriftFlag?> LoadFlagAsync(string symbol, Timeframe timeframe, bool includeDeleted = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAdwinDriftWorkerTestContext>();

            IQueryable<MLDriftFlag> query = db.Set<MLDriftFlag>().AsNoTracking();
            if (includeDeleted)
                query = query.IgnoreQueryFilters();

            return await query.SingleOrDefaultAsync(f =>
                f.Symbol == symbol &&
                f.Timeframe == timeframe &&
                f.DetectorType == "AdwinDrift");
        }

        public async Task<Alert?> LoadAlertAsync(string dedupeKey)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAdwinDriftWorkerTestContext>();
            return await db.Set<Alert>()
                .AsNoTracking()
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.DeduplicationKey == dedupeKey);
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLAdwinDriftWorkerTestContext(DbContextOptions<MLAdwinDriftWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(config => config.Id);
                builder.HasQueryFilter(config => !config.IsDeleted);
                builder.Property(config => config.DataType).HasConversion<string>();
                builder.HasIndex(config => config.Key).IsUnique();
            });

            modelBuilder.Entity<MLModel>(builder =>
            {
                builder.HasKey(model => model.Id);
                builder.HasQueryFilter(model => !model.IsDeleted);
                builder.Property(model => model.Timeframe).HasConversion<string>();
                builder.Property(model => model.Status).HasConversion<string>();
                builder.Property(model => model.LearnerArchitecture).HasConversion<string>();
                builder.Property(model => model.RowVersion).HasDefaultValue(0u).ValueGeneratedNever();

                builder.Ignore(model => model.TrainingRuns);
                builder.Ignore(model => model.TradeSignals);
                builder.Ignore(model => model.PredictionLogs);
                builder.Ignore(model => model.ChampionEvaluations);
                builder.Ignore(model => model.ChallengerEvaluations);
                builder.Ignore(model => model.CausalFeatureAudits);
                builder.Ignore(model => model.ConformalCalibrations);
                builder.Ignore(model => model.FeatureInteractionAudits);
                builder.Ignore(model => model.LifecycleLogs);
            });

            modelBuilder.Entity<MLModelPredictionLog>(builder =>
            {
                builder.HasKey(log => log.Id);
                builder.HasQueryFilter(log => !log.IsDeleted);
                builder.Property(log => log.ModelRole).HasConversion<string>();
                builder.Property(log => log.Timeframe).HasConversion<string>();
                builder.Property(log => log.PredictedDirection).HasConversion<string>();
                builder.Property(log => log.ActualDirection).HasConversion<string>();

                builder.Ignore(log => log.TradeSignal);
                builder.Ignore(log => log.MLModel);
                builder.Ignore(log => log.MLConformalCalibration);
            });

            modelBuilder.Entity<MLTrainingRun>(builder =>
            {
                builder.HasKey(run => run.Id);
                builder.HasQueryFilter(run => !run.IsDeleted);
                builder.Property(run => run.Timeframe).HasConversion<string>();
                builder.Property(run => run.TriggerType).HasConversion<string>();
                builder.Property(run => run.Status).HasConversion<string>();
                builder.Property(run => run.LearnerArchitecture).HasConversion<string>();

                builder.Ignore(run => run.MLModel);
            });

            modelBuilder.Entity<MLAdwinDriftLog>(builder =>
            {
                builder.HasKey(log => log.Id);
                builder.HasQueryFilter(log => !log.IsDeleted);
                builder.Property(log => log.Timeframe).HasConversion<string>();
                builder.Property(log => log.DominantRegime).HasConversion<string>();

                builder.Ignore(log => log.MLModel);
            });

            modelBuilder.Entity<MLDriftFlag>(builder =>
            {
                builder.HasKey(f => f.Id);
                builder.HasQueryFilter(f => !f.IsDeleted);
                builder.Property(f => f.Timeframe).HasConversion<string>();
                builder.HasIndex(f => new { f.Symbol, f.Timeframe, f.DetectorType }).IsUnique();
            });

            modelBuilder.Entity<MarketRegimeSnapshot>(builder =>
            {
                builder.HasKey(s => s.Id);
                builder.HasQueryFilter(s => !s.IsDeleted);
                builder.Property(s => s.Timeframe).HasConversion<string>();
                builder.Property(s => s.Regime).HasConversion<string>();
            });

            modelBuilder.Entity<Alert>(builder =>
            {
                builder.HasKey(a => a.Id);
                builder.HasQueryFilter(a => !a.IsDeleted);
                builder.Property(a => a.AlertType).HasConversion<string>();
                builder.Property(a => a.Severity).HasConversion<string>();
                builder.HasIndex(a => a.DeduplicationKey);
            });
        }
    }

    private sealed class TestDistributedLock(bool lockAvailable) : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(lockAvailable ? new Releaser() : null);

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
            => TryAcquireAsync(lockKey, ct);

        private sealed class Releaser : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAlertDispatcher : IAlertDispatcher
    {
        public List<(Alert alert, string message)> Dispatched { get; } = new();

        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            Dispatched.Add((alert, message));
            return Task.CompletedTask;
        }

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
            => Task.CompletedTask;
    }
}
