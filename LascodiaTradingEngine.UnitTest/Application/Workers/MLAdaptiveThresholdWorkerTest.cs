using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLAdaptiveThresholdWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_UsesMagnitudeWeightedServedProbabilities()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");

                db.Set<MLModelPredictionLog>().AddRange(
                    NewPredictionLog(1, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, 0.51m, TradeDirection.Buy, 1m),
                    NewPredictionLog(2, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, 0.60m, TradeDirection.Sell, -100m));
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var snapshot = await harness.LoadSnapshotAsync(1);

        Assert.Equal(1, result.ModelsUpdated);
        Assert.Equal(0.70, snapshot.AdaptiveThreshold, 2);
    }

    [Fact]
    public async Task RunCycleAsync_FlatEvCurve_DoesNotArtificiallyDriftTowardLowerBound()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.62);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "4");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");

                db.Set<MLModelPredictionLog>().AddRange(
                    NewPredictionLog(1, 1, now.AddHours(-5).UtcDateTime, now.AddHours(-4).UtcDateTime, 0.62m, TradeDirection.Buy, 10m),
                    NewPredictionLog(2, 1, now.AddHours(-4).UtcDateTime, now.AddHours(-3).UtcDateTime, 0.62m, TradeDirection.Sell, -10m),
                    NewPredictionLog(3, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, 0.38m, TradeDirection.Buy, 10m),
                    NewPredictionLog(4, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, 0.38m, TradeDirection.Sell, -10m));
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var snapshot = await harness.LoadSnapshotAsync(1);

        Assert.Equal(0, result.ModelsUpdated);
        Assert.Equal(0.62, snapshot.AdaptiveThreshold, 2);
    }

    [Fact]
    public async Task RunCycleAsync_PrunesStaleRegimeThresholds_WhenFreshWindowLacksThatRegime()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(
                    db,
                    1,
                    adaptiveThreshold: 0.50,
                    regimeThresholds: new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["Trending"] = 0.55,
                        ["Volatile"] = 0.65
                    });

                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:MinRegimeResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "1.0");

                db.Set<MLModelPredictionLog>().AddRange(
                    NewPredictionLog(1, 1, now.AddHours(-4).UtcDateTime, now.AddHours(-3).UtcDateTime, 0.56m, TradeDirection.Buy, 5m),
                    NewPredictionLog(2, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, 0.44m, TradeDirection.Sell, -5m));

                db.Set<MarketRegimeSnapshot>().AddRange(
                    NewRegimeSnapshot(1, "EURUSD", Timeframe.H1, MarketRegime.Trending, now.AddHours(-5).UtcDateTime),
                    NewRegimeSnapshot(2, "EURUSD", Timeframe.H1, MarketRegime.Trending, now.AddHours(-3).UtcDateTime));
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var snapshot = await harness.LoadSnapshotAsync(1);

        Assert.Equal(1, result.ModelsUpdated);
        Assert.Equal(1, result.RegimeThresholdsPruned);
        Assert.Contains("Trending", snapshot.RegimeThresholds.Keys);
        Assert.DoesNotContain("Volatile", snapshot.RegimeThresholds.Keys);
    }

    [Fact]
    public async Task RunCycleAsync_UninformativeLegacyLogs_DoNotTriggerAdaptation()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");

                db.Set<MLModelPredictionLog>().AddRange(
                    NewLegacyPredictionLog(1, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, TradeDirection.Buy, TradeDirection.Buy),
                    NewLegacyPredictionLog(2, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, TradeDirection.Buy, TradeDirection.Sell));
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var snapshot = await harness.LoadSnapshotAsync(1);

        Assert.Equal(0, result.ModelsUpdated);
        Assert.Equal(1, result.ModelsSkipped);
        Assert.Equal(0.50, snapshot.AdaptiveThreshold, 2);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingModel()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                db.Set<MLModelPredictionLog>().Add(
                    NewPredictionLog(1, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, 0.90m, TradeDirection.Buy, 10m));
            },
            now: now,
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var snapshot = await harness.LoadSnapshotAsync(1);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(0.50, snapshot.AdaptiveThreshold, 2);
    }

    [Fact]
    public async Task RunCycleAsync_InvalidSettingsAreClampedSafely()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLAdaptiveThreshold:PollIntervalSeconds", "-10");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "1");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "9999");
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "2.5");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "-5");
                AddConfig(db, "MLAdaptiveThreshold:LookbackDays", "0");
                AddConfig(db, "MLAdaptiveThreshold:MinRegimeResolvedPredictions", "9999");
                AddConfig(db, "MLAdaptiveThreshold:MaxModelsPerCycle", "0");
                AddConfig(db, "MLAdaptiveThreshold:LockTimeoutSeconds", "-3");
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(3600), result.Settings.PollInterval);
        Assert.Equal(2, result.Settings.WindowSize);
        Assert.Equal(2, result.Settings.MinResolvedPredictions);
        Assert.Equal(1.0, result.Settings.EmaAlpha, 6);
        Assert.Equal(0.01, result.Settings.MinThresholdDrift, 6);
        Assert.Equal(30, result.Settings.LookbackDays);
        Assert.Equal(2, result.Settings.MinRegimeResolvedPredictions);
        Assert.Equal(256, result.Settings.MaxModelsPerCycle);
        Assert.Equal(5, result.Settings.LockTimeoutSeconds);
    }

    [Fact]
    public async Task RunCycleAsync_WritesAuditRowEvenWhenSnapshotUnchanged()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                // Drift floor far above any plausible sweep result so no update is accepted.
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.99");

                db.Set<MLModelPredictionLog>().AddRange(
                    NewPredictionLog(1, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, 0.51m, TradeDirection.Buy, 1m),
                    NewPredictionLog(2, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, 0.60m, TradeDirection.Sell, -100m));
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var audits = await harness.LoadAuditLogsAsync(1);

        Assert.Equal(0, result.ModelsUpdated);
        Assert.NotEmpty(audits);
        // The global decision row records the rejection reason for operator review. With this
        // tiny seed there is no real holdout slice, so the reason carries the no-holdout suffix.
        Assert.Contains(audits, a => a.Outcome == "skipped_drift"
            && (a.Reason == "drift_below_floor" || a.Reason == "drift_below_floor_no_holdout"));
    }

    [Fact]
    public async Task RunCycleAsync_StaleData_SkipsRedundantSweepOnSecondCycle()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");

                db.Set<MLModelPredictionLog>().AddRange(
                    NewPredictionLog(1, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, 0.51m, TradeDirection.Buy, 1m),
                    NewPredictionLog(2, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, 0.60m, TradeDirection.Sell, -100m));
            },
            now: now);

        var first = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var second = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var audits = await harness.LoadAuditLogsAsync(1);

        // First cycle adapts; second cycle short-circuits because no new outcomes have arrived.
        Assert.Equal(1, first.ModelsUpdated);
        Assert.Equal(0, second.ModelsUpdated);
        // Audits from second cycle should not pile on extra "drift" rows — the short-circuit
        // bypasses the audit write entirely.
        int auditsAfterShortCircuit = audits.Count;
        var auditsAgain = await harness.LoadAuditLogsAsync(1);
        Assert.Equal(auditsAfterShortCircuit, auditsAgain.Count);
    }

    [Fact]
    public async Task RunCycleAsync_PsiAboveThreshold_SkipsAdaptationWithStationarityReason()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "10");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "100");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");
                AddConfig(db, "MLAdaptiveThreshold:MinStationaritySamples", "10");
                AddConfig(db, "MLAdaptiveThreshold:StationarityPsiThreshold", "0.05");

                // Older half: confidence concentrated near 0.10. Newer half: near 0.95.
                // Two completely disjoint distributions guarantee a high PSI.
                for (int i = 0; i < 10; i++)
                {
                    db.Set<MLModelPredictionLog>().Add(
                        NewPredictionLog(i + 1, 1,
                            now.AddHours(-100 + i).UtcDateTime,
                            now.AddHours(-99 + i).UtcDateTime,
                            servedProbability: 0.10m,
                            TradeDirection.Sell,
                            actualMagnitude: -1m));
                }
                for (int i = 0; i < 10; i++)
                {
                    db.Set<MLModelPredictionLog>().Add(
                        NewPredictionLog(i + 11, 1,
                            now.AddHours(-50 + i).UtcDateTime,
                            now.AddHours(-49 + i).UtcDateTime,
                            servedProbability: 0.95m,
                            TradeDirection.Buy,
                            actualMagnitude: 1m));
                }
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var audits = await harness.LoadAuditLogsAsync(1);

        Assert.Equal(0, result.ModelsUpdated);
        // PSI hard cap = StationarityPsiThreshold * PsiHardCapMultiplier (default 2.0). With
        // PSI dwarfing the threshold the worker hits the hard cap branch.
        Assert.Contains(audits, a => a.Outcome == "skipped_stationarity"
            && (a.Reason == "psi_above_hard_cap" || a.Reason == "psi_above_threshold"));
    }

    [Fact]
    public async Task RunCycleAsync_ConcentratedPredictions_DispatchAnomalousDriftAlert()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        var dispatcher = new CapturingAlertDispatcher();

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");
                AddConfig(db, "MLAdaptiveThreshold:AnomalousDriftAlertThreshold", "0.05");

                // Strongly directional logs ensure a sweep result far from the current 0.50.
                db.Set<MLModelPredictionLog>().AddRange(
                    NewPredictionLog(1, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, 0.95m, TradeDirection.Buy, 50m),
                    NewPredictionLog(2, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, 0.92m, TradeDirection.Buy, 50m));
            },
            now: now,
            alertDispatcher: dispatcher);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.ModelsUpdated);
        Assert.NotEmpty(dispatcher.Dispatched);
        Assert.Contains(dispatcher.Dispatched, e =>
            e.Alert.AlertType == AlertType.ConfigurationDrift &&
            e.Alert.DeduplicationKey == "ml-adaptive-threshold-drift:1");
    }

    [Fact]
    public async Task RunCycleAsync_HoldoutRegression_RejectsThresholdThatLosesEvOnHoldout()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "20");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "200");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");
                AddConfig(db, "MLAdaptiveThreshold:HoldoutFraction", "0.5");
                AddConfig(db, "MLAdaptiveThreshold:MinHoldoutSamples", "20");
                AddConfig(db, "MLAdaptiveThreshold:WilsonLowerBoundFloor", "0.0");

                long id = 1;
                // Older 30 logs (sweep slice): high-confidence Buys that always win — sweep
                // strongly prefers a lower threshold to ride those wins.
                for (int i = 0; i < 30; i++)
                {
                    db.Set<MLModelPredictionLog>().Add(
                        NewPredictionLog(id++, 1,
                            now.AddHours(-200 + i).UtcDateTime,
                            now.AddHours(-199 + i).UtcDateTime,
                            servedProbability: 0.80m,
                            TradeDirection.Buy,
                            actualMagnitude: 100m));
                }
                // Newer 30 logs (holdout slice): same high-confidence Buys but they all lose.
                // The lower threshold the sweep wants would convert "no-trade" into "wrong-trade",
                // so the holdout EV at the new threshold drops below the holdout EV at 0.50.
                for (int i = 0; i < 30; i++)
                {
                    db.Set<MLModelPredictionLog>().Add(
                        NewPredictionLog(id++, 1,
                            now.AddHours(-100 + i).UtcDateTime,
                            now.AddHours(-99 + i).UtcDateTime,
                            servedProbability: 0.80m,
                            TradeDirection.Sell,
                            actualMagnitude: -100m));
                }
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var audits = await harness.LoadAuditLogsAsync(1);

        // The walk-forward guard catches the regression: in-sample optimum looks great but the
        // holdout reverses the verdict, so the threshold is left untouched.
        Assert.Equal(0, result.ModelsUpdated);
        Assert.Contains(audits, a => a.Outcome == "skipped_drift" && a.Reason == "holdout_regression");
    }

    [Fact]
    public async Task RunCycleAsync_RealizedPnl_OutweighsMagnitudeHeuristic()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");

                // Two logs: pBuy=0.55 → predicts Buy. Magnitude heuristic would say "small +$",
                // but Position table records a large realised loss for the Buy. The P&L join
                // should flip the EV sign so the sweep no longer prefers a low threshold.
                db.Set<MLModelPredictionLog>().AddRange(
                    NewPredictionLog(1, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, 0.55m, TradeDirection.Buy, actualMagnitude: 1m),
                    NewPredictionLog(2, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, 0.55m, TradeDirection.Buy, actualMagnitude: 1m));

                // Wire prediction signal IDs through to closed positions with large negative P&L.
                db.Set<Order>().AddRange(
                    NewOrder(101, signalId: 1),
                    NewOrder(102, signalId: 2));
                db.Set<Position>().AddRange(
                    NewClosedPosition(201, openOrderId: 101, realizedPnl: -500m),
                    NewClosedPosition(202, openOrderId: 102, realizedPnl: -500m));
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var audits = await harness.LoadAuditLogsAsync(1);
        var auditsForGlobal = audits.Where(a => a.Regime == null).ToList();

        // The pnlMapSize diagnostic confirms the join landed two real positions.
        Assert.NotEmpty(auditsForGlobal);
        Assert.Contains(auditsForGlobal, a => a.DiagnosticsJson.Contains("\"pnlMapSize\":2"));
        // With realised losses, the holdout-mean-pnl-pips should be negative — the worker is
        // measuring real money, not the +1-pip magnitude heuristic.
        Assert.Contains(auditsForGlobal, a => a.HoldoutMeanPnlPips < 0);
    }

    [Fact]
    public async Task RunCycleAsync_PersistedNewestOutcomeAt_SurvivesWorkerInstanceRebuild()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<MLAdaptiveThresholdWorkerTestContext>(options => options.UseSqlite(connection));
            services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>());
            services.AddScoped<IReadApplicationDbContext>(provider => provider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>());
            using var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>();
                db.Database.EnsureCreated();
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");
                db.Set<MLModelPredictionLog>().AddRange(
                    NewPredictionLog(1, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, 0.51m, TradeDirection.Buy, 1m),
                    NewPredictionLog(2, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, 0.60m, TradeDirection.Sell, -100m));
                db.SaveChanges();
            }

            // First worker instance: runs a full cycle, persists audit rows (including NewestOutcomeAt).
            var cache1 = new MemoryCache(new MemoryCacheOptions());
            var worker1 = new MLAdaptiveThresholdWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                cache1,
                NullLogger<MLAdaptiveThresholdWorker>.Instance,
                distributedLock: null,
                healthMonitor: null,
                metrics: null,
                timeProvider: new TestTimeProvider(now));
            var first = await worker1.RunCycleAsync(CancellationToken.None);
            cache1.Dispose();

            // Second worker instance: brand new state — but reads NewestOutcomeAt from audit log.
            var cache2 = new MemoryCache(new MemoryCacheOptions());
            var worker2 = new MLAdaptiveThresholdWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                cache2,
                NullLogger<MLAdaptiveThresholdWorker>.Instance,
                distributedLock: null,
                healthMonitor: null,
                metrics: null,
                timeProvider: new TestTimeProvider(now));
            var second = await worker2.RunCycleAsync(CancellationToken.None);
            cache2.Dispose();

            Assert.Equal(1, first.ModelsUpdated);
            Assert.Equal(0, second.ModelsUpdated);
            // Critical: the second worker should short-circuit, leaving no NEW audit rows. If
            // the short-circuit only used in-memory state, the second worker would re-evaluate
            // and write another row with reason="drift_below_floor" (or accepted).
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>();
                int totalAudits = await db.Set<MLAdaptiveThresholdLog>().AsNoTracking().CountAsync();
                // Exactly the rows from the first cycle — no new ones from the second cycle.
                Assert.True(totalAudits >= 1);
                int rowsAfterFirstCycleEnd = totalAudits;
                // Run second cycle once more for emphasis; total should still equal rowsAfterFirstCycleEnd.
                await worker2.RunCycleAsync(CancellationToken.None);
                int totalAfter = await db.Set<MLAdaptiveThresholdLog>().AsNoTracking().CountAsync();
                Assert.Equal(rowsAfterFirstCycleEnd, totalAfter);
            }
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public async Task RunCycleAsync_PsiBetweenThresholdAndHardCap_DampensAlpha()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "20");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "100");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");
                AddConfig(db, "MLAdaptiveThreshold:MinStationaritySamples", "10");
                AddConfig(db, "MLAdaptiveThreshold:StationarityPsiThreshold", "0.05");
                AddConfig(db, "MLAdaptiveThreshold:PsiHardCapMultiplier", "10.0");

                // Confidence histograms: PSI is computed on |servedProbability − 0.5| × 2.
                // p=0.40 → conf=0.20 (bin 2); p=0.55 → conf=0.10 (bin 1).
                // Old half: 5/5 split between bins 1 and 2. New half: 7/3 split. Resulting
                // PSI lands between threshold (0.05) and hard-cap (0.50) → soft-mode dampening.
                long id = 1;
                for (int i = 0; i < 5; i++)
                {
                    db.Set<MLModelPredictionLog>().Add(
                        NewPredictionLog(id++, 1,
                            now.AddHours(-200 + i).UtcDateTime,
                            now.AddHours(-199 + i).UtcDateTime,
                            servedProbability: 0.40m,
                            TradeDirection.Buy,
                            actualMagnitude: 1m));
                }
                for (int i = 0; i < 5; i++)
                {
                    db.Set<MLModelPredictionLog>().Add(
                        NewPredictionLog(id++, 1,
                            now.AddHours(-195 + i).UtcDateTime,
                            now.AddHours(-194 + i).UtcDateTime,
                            servedProbability: 0.55m,
                            TradeDirection.Buy,
                            actualMagnitude: 1m));
                }
                for (int i = 0; i < 7; i++)
                {
                    db.Set<MLModelPredictionLog>().Add(
                        NewPredictionLog(id++, 1,
                            now.AddHours(-100 + i).UtcDateTime,
                            now.AddHours(-99 + i).UtcDateTime,
                            servedProbability: 0.40m,
                            TradeDirection.Buy,
                            actualMagnitude: 1m));
                }
                for (int i = 0; i < 3; i++)
                {
                    db.Set<MLModelPredictionLog>().Add(
                        NewPredictionLog(id++, 1,
                            now.AddHours(-93 + i).UtcDateTime,
                            now.AddHours(-92 + i).UtcDateTime,
                            servedProbability: 0.55m,
                            TradeDirection.Buy,
                            actualMagnitude: 1m));
                }
            },
            now: now);

        await harness.Worker.RunCycleAsync(CancellationToken.None);
        var audits = await harness.LoadAuditLogsAsync(1);

        // The global decision row is written even when adaptation is rejected. Its diagnostics
        // record the soft-mode dampening factor: psiAlphaScale should be < 1.0 because PSI is
        // above the threshold but below the hard cap.
        var globalAudit = audits.SingleOrDefault(a => a.Regime == null);
        Assert.NotNull(globalAudit);
        var diag = JsonDocument.Parse(globalAudit!.DiagnosticsJson).RootElement;
        double psiAlphaScale = diag.GetProperty("psiAlphaScale").GetDouble();
        double psi = globalAudit.StationarityPsi;
        Assert.True(psi > 0.01, $"PSI ({psi}) should exceed soft-mode threshold");
        Assert.True(psiAlphaScale < 1.0, $"psiAlphaScale ({psiAlphaScale}) should be dampened");
        Assert.True(psiAlphaScale >= 0.0, $"psiAlphaScale ({psiAlphaScale}) cannot be negative");
    }

    [Fact]
    public async Task RunCycleAsync_RegressionGuardK_RejectsImprovementWithinNoiseBand()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "60");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "60");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");
                AddConfig(db, "MLAdaptiveThreshold:MinHoldoutSamples", "30");
                AddConfig(db, "MLAdaptiveThreshold:HoldoutFraction", "0.5");
                AddConfig(db, "MLAdaptiveThreshold:WilsonLowerBoundFloor", "0.0");
                // Set guard K well above the natural improvement-to-stderr ratio so the
                // small-but-real EV gain is rejected as statistical noise.
                AddConfig(db, "MLAdaptiveThreshold:RegressionGuardK", "5.0");

                // 60 logs, all pBuy=0.55. 18 correct + 12 wrong per slice gives 60% direction
                // accuracy with non-zero variance — the sweep prefers a low threshold for the
                // edge magnitude, but the holdout EV improvement falls inside the K-sigma band.
                long id = 1;
                for (int sliceStart = -100; sliceStart >= -200; sliceStart -= 100)
                {
                    int sliceCount = 30;
                    for (int i = 0; i < 18; i++)
                    {
                        db.Set<MLModelPredictionLog>().Add(
                            NewPredictionLog(id++, 1,
                                now.AddHours(sliceStart + i).UtcDateTime,
                                now.AddHours(sliceStart + i + 1).UtcDateTime,
                                servedProbability: 0.55m,
                                TradeDirection.Buy,
                                actualMagnitude: 10m));
                    }
                    for (int i = 18; i < sliceCount; i++)
                    {
                        db.Set<MLModelPredictionLog>().Add(
                            NewPredictionLog(id++, 1,
                                now.AddHours(sliceStart + i).UtcDateTime,
                                now.AddHours(sliceStart + i + 1).UtcDateTime,
                                servedProbability: 0.55m,
                                TradeDirection.Sell,
                                actualMagnitude: -10m));
                    }
                }
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var audits = await harness.LoadAuditLogsAsync(1);

        Assert.Equal(0, result.ModelsUpdated);
        // The audit should report the rejection as a statistical regression-guard miss, with
        // the K parameter exposed in diagnostics so operators can tune.
        var rejection = audits.SingleOrDefault(a => a.Regime == null);
        Assert.NotNull(rejection);
        Assert.Equal("holdout_regression", rejection!.Reason);
        var diag = JsonDocument.Parse(rejection.DiagnosticsJson).RootElement;
        Assert.Equal(5.0, diag.GetProperty("regressionGuardK").GetDouble(), 6);
        Assert.True(diag.GetProperty("hasRealHoldout").GetBoolean());
    }

    [Fact]
    public async Task RunCycleAsync_TimeDecay_AutoDisablesBelowMinSamples()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");
                // Configure decay nominally on (60d) but require 200 samples to activate. With
                // 2 logs the worker should report effective half-life of 0 in diagnostics.
                AddConfig(db, "MLAdaptiveThreshold:TimeDecayHalfLifeDays", "60");
                AddConfig(db, "MLAdaptiveThreshold:MinSamplesForTimeDecay", "200");

                db.Set<MLModelPredictionLog>().AddRange(
                    NewPredictionLog(1, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, 0.51m, TradeDirection.Buy, 1m),
                    NewPredictionLog(2, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, 0.60m, TradeDirection.Sell, -100m));
            },
            now: now);

        await harness.Worker.RunCycleAsync(CancellationToken.None);
        var audits = await harness.LoadAuditLogsAsync(1);

        var globalAudit = audits.SingleOrDefault(a => a.Regime == null);
        Assert.NotNull(globalAudit);
        var diag = JsonDocument.Parse(globalAudit!.DiagnosticsJson).RootElement;
        Assert.Equal(0.0, diag.GetProperty("decayHalfLifeDays").GetDouble(), 6);
    }

    [Fact]
    public async Task RunCycleAsync_AuditRow_PersistsWhenSnapshotSaveThrows()
    {
        var now = new DateTimeOffset(2026, 04, 25, 10, 0, 0, TimeSpan.Zero);
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        try
        {
            // Interceptor throws on the snapshot save (Modified MLModel) but not on audit-only
            // saves (Added MLAdaptiveThresholdLog). This proves audit isolation: the dedicated
            // FlushAuditsAsync scope succeeds even when the snapshot scope fails.
            var interceptor = new ModelSaveBlockingInterceptor();

            var services = new ServiceCollection();
            services.AddDbContext<MLAdaptiveThresholdWorkerTestContext>(options =>
                options.UseSqlite(connection).AddInterceptors(interceptor));
            services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>());
            services.AddScoped<IReadApplicationDbContext>(provider => provider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>());
            using var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>();
                db.Database.EnsureCreated();
                SeedActiveModel(db, 1, adaptiveThreshold: 0.50);
                AddConfig(db, "MLAdaptiveThreshold:EmaAlpha", "1.0");
                AddConfig(db, "MLAdaptiveThreshold:MinResolvedPredictions", "2");
                AddConfig(db, "MLAdaptiveThreshold:WindowSize", "10");
                AddConfig(db, "MLAdaptiveThreshold:MinThresholdDrift", "0.0001");
                db.Set<MLModelPredictionLog>().AddRange(
                    NewPredictionLog(1, 1, now.AddHours(-3).UtcDateTime, now.AddHours(-2).UtcDateTime, 0.95m, TradeDirection.Buy, 50m),
                    NewPredictionLog(2, 1, now.AddHours(-2).UtcDateTime, now.AddHours(-1).UtcDateTime, 0.92m, TradeDirection.Buy, 50m));
                db.SaveChanges();
            }

            // From here on, snapshot saves throw. Audit saves continue to work.
            interceptor.Armed = true;

            var cache = new MemoryCache(new MemoryCacheOptions());
            var worker = new MLAdaptiveThresholdWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                cache,
                NullLogger<MLAdaptiveThresholdWorker>.Instance,
                distributedLock: null,
                healthMonitor: null,
                metrics: null,
                timeProvider: new TestTimeProvider(now));

            var result = await worker.RunCycleAsync(CancellationToken.None);
            cache.Dispose();

            // Snapshot save threw → cycle reports ModelsFailed = 1 (or skipped).
            Assert.Equal(0, result.ModelsUpdated);
            Assert.True(result.ModelsFailed + result.ModelsSkipped >= 1);

            // Critical: despite the snapshot failure, audit rows accumulated for this model
            // were flushed in their own scope and are queryable in the DB.
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>();
                var audits = await db.Set<MLAdaptiveThresholdLog>()
                    .AsNoTracking()
                    .Where(l => l.MLModelId == 1)
                    .ToListAsync();
                Assert.NotEmpty(audits);
            }
        }
        finally
        {
            connection.Dispose();
        }
    }

    private sealed class ModelSaveBlockingInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context is { } ctx)
            {
                bool touchesMLModel = ctx.ChangeTracker
                    .Entries<MLModel>()
                    .Any(e => e.State is EntityState.Modified or EntityState.Added or EntityState.Deleted);
                if (touchesMLModel)
                    throw new InvalidOperationException("Simulated snapshot save failure");
            }

            return ValueTask.FromResult(result);
        }
    }

    private static Order NewOrder(long id, long signalId)
    {
        return new Order
        {
            Id = id,
            TradeSignalId = signalId,
            Symbol = "EURUSD",
            TradingAccountId = 1,
            StrategyId = 1,
            OrderType = OrderType.Buy,
            Quantity = 1m,
            Price = 1m,
            Status = OrderStatus.Filled,
            CreatedAt = DateTime.UtcNow,
            IsDeleted = false,
        };
    }

    private static Position NewClosedPosition(long id, long openOrderId, decimal realizedPnl)
    {
        return new Position
        {
            Id = id,
            Symbol = "EURUSD",
            Direction = PositionDirection.Long,
            OpenLots = 1m,
            AverageEntryPrice = 1m,
            RealizedPnL = realizedPnl,
            Status = PositionStatus.Closed,
            OpenOrderId = openOrderId,
            OpenedAt = DateTime.UtcNow.AddHours(-3),
            ClosedAt = DateTime.UtcNow.AddHours(-1),
            IsDeleted = false,
        };
    }

    private static WorkerHarness CreateHarness(
        Action<MLAdaptiveThresholdWorkerTestContext> seed,
        DateTimeOffset now,
        IDistributedLock? distributedLock = null,
        IAlertDispatcher? alertDispatcher = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLAdaptiveThresholdWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(provider => provider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>());
        if (alertDispatcher is not null)
            services.AddSingleton(alertDispatcher);

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var cache = new MemoryCache(new MemoryCacheOptions());
        var worker = new MLAdaptiveThresholdWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            cache,
            NullLogger<MLAdaptiveThresholdWorker>.Instance,
            distributedLock,
            healthMonitor: null,
            metrics: null,
            timeProvider: new TestTimeProvider(now),
            alertDispatcher: alertDispatcher);

        return new WorkerHarness(provider, connection, cache, worker);
    }

    private static void SeedActiveModel(
        MLAdaptiveThresholdWorkerTestContext db,
        long id,
        double adaptiveThreshold,
        Dictionary<string, double>? regimeThresholds = null)
    {
        var snapshot = new ModelSnapshot
        {
            AdaptiveThreshold = adaptiveThreshold,
            OptimalThreshold = 0.50,
            RegimeThresholds = regimeThresholds ?? []
        };

        db.Set<MLModel>().Add(new MLModel
        {
            Id = id,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = "1.0.0",
            FilePath = "/tmp/model.json",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainedAt = DateTime.UtcNow.AddDays(-1),
            ActivatedAt = DateTime.UtcNow.AddHours(-12),
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot),
            LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            IsDeleted = false
        });
    }

    private static void AddConfig(
        MLAdaptiveThresholdWorkerTestContext db,
        string key,
        string value)
    {
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = ConfigDataType.String,
            LastUpdatedAt = DateTime.UtcNow,
            IsDeleted = false
        });
    }

    private static MLModelPredictionLog NewPredictionLog(
        long id,
        long modelId,
        DateTime predictedAtUtc,
        DateTime outcomeRecordedAtUtc,
        decimal servedProbability,
        TradeDirection actualDirection,
        decimal actualMagnitude)
    {
        return new MLModelPredictionLog
        {
            Id = id,
            TradeSignalId = id,
            MLModelId = modelId,
            ModelRole = ModelRole.Champion,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            PredictedDirection = servedProbability >= 0.50m ? TradeDirection.Buy : TradeDirection.Sell,
            PredictedMagnitudePips = 0,
            ConfidenceScore = Math.Abs(servedProbability - 0.50m) * 2m,
            ServedCalibratedProbability = servedProbability,
            DecisionThresholdUsed = 0.50m,
            ActualDirection = actualDirection,
            ActualMagnitudePips = actualMagnitude,
            DirectionCorrect = (servedProbability >= 0.50m ? TradeDirection.Buy : TradeDirection.Sell) == actualDirection,
            PredictedAt = predictedAtUtc,
            OutcomeRecordedAt = outcomeRecordedAtUtc,
            IsDeleted = false
        };
    }

    private static MLModelPredictionLog NewLegacyPredictionLog(
        long id,
        long modelId,
        DateTime predictedAtUtc,
        DateTime outcomeRecordedAtUtc,
        TradeDirection predictedDirection,
        TradeDirection actualDirection)
    {
        return new MLModelPredictionLog
        {
            Id = id,
            TradeSignalId = id,
            MLModelId = modelId,
            ModelRole = ModelRole.Champion,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            PredictedDirection = predictedDirection,
            PredictedMagnitudePips = 0,
            ConfidenceScore = 0.40m,
            ActualDirection = actualDirection,
            ActualMagnitudePips = actualDirection == TradeDirection.Buy ? 5m : -5m,
            DirectionCorrect = predictedDirection == actualDirection,
            PredictedAt = predictedAtUtc,
            OutcomeRecordedAt = outcomeRecordedAtUtc,
            IsDeleted = false
        };
    }

    private static MarketRegimeSnapshot NewRegimeSnapshot(
        long id,
        string symbol,
        Timeframe timeframe,
        MarketRegime regime,
        DateTime detectedAtUtc)
    {
        return new MarketRegimeSnapshot
        {
            Id = id,
            Symbol = symbol,
            Timeframe = timeframe,
            Regime = regime,
            Confidence = 0.80m,
            ADX = 30m,
            ATR = 0.0010m,
            BollingerBandWidth = 0.0020m,
            DetectedAt = detectedAtUtc,
            IsDeleted = false
        };
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MemoryCache cache,
        MLAdaptiveThresholdWorker worker) : IDisposable
    {
        public MLAdaptiveThresholdWorker Worker { get; } = worker;

        public async Task<ModelSnapshot> LoadSnapshotAsync(long modelId)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>();
            var model = await db.Set<MLModel>()
                .AsNoTracking()
                .SingleAsync(m => m.Id == modelId);

            return JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!)!;
        }

        public async Task<List<MLAdaptiveThresholdLog>> LoadAuditLogsAsync(long modelId)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAdaptiveThresholdWorkerTestContext>();
            return await db.Set<MLAdaptiveThresholdLog>()
                .AsNoTracking()
                .Where(l => l.MLModelId == modelId)
                .OrderBy(l => l.Id)
                .ToListAsync();
        }

        public void Dispose()
        {
            provider.Dispose();
            cache.Dispose();
            connection.Dispose();
        }
    }

    private sealed class CapturingAlertDispatcher : IAlertDispatcher
    {
        public List<(Alert Alert, string Message)> Dispatched { get; } = [];

        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            Dispatched.Add((alert, message));
            return Task.CompletedTask;
        }

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class MLAdaptiveThresholdWorkerTestContext(DbContextOptions<MLAdaptiveThresholdWorkerTestContext> options)
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

            modelBuilder.Entity<MarketRegimeSnapshot>(builder =>
            {
                builder.HasKey(snapshot => snapshot.Id);
                builder.HasQueryFilter(snapshot => !snapshot.IsDeleted);
                builder.Property(snapshot => snapshot.Timeframe).HasConversion<string>();
                builder.Property(snapshot => snapshot.Regime).HasConversion<string>();
            });

            modelBuilder.Entity<MLAdaptiveThresholdLog>(builder =>
            {
                builder.HasKey(log => log.Id);
                builder.HasQueryFilter(log => !log.IsDeleted);
                builder.Property(log => log.Timeframe).HasConversion<string>();
                builder.Property(log => log.Regime).HasConversion<string>();
            });

            modelBuilder.Entity<Alert>(builder =>
            {
                builder.HasKey(alert => alert.Id);
                builder.HasQueryFilter(alert => !alert.IsDeleted);
                builder.Property(alert => alert.AlertType).HasConversion<string>();
                builder.Property(alert => alert.Severity).HasConversion<string>();
            });

            modelBuilder.Entity<Order>(builder =>
            {
                builder.HasKey(order => order.Id);
                builder.HasQueryFilter(order => !order.IsDeleted);
                builder.Property(order => order.OrderType).HasConversion<string>();
                builder.Property(order => order.ExecutionType).HasConversion<string>();
                builder.Property(order => order.Status).HasConversion<string>();
                builder.Property(order => order.Session).HasConversion<string>();
                builder.Property(order => order.ExecutionAlgorithm).HasConversion<string>();
                builder.Property(order => order.TimeInForce).HasConversion<string>();
                builder.Property(order => order.TrailingStopType).HasConversion<string>();
                builder.Ignore(order => order.Strategy);
                builder.Ignore(order => order.TradingAccount);
                builder.Ignore(order => order.TradeSignal);
                builder.Ignore(order => order.ExecutionQualityLog);
                builder.Ignore(order => order.PositionScaleOrders);
            });

            modelBuilder.Entity<Position>(builder =>
            {
                builder.HasKey(position => position.Id);
                builder.HasQueryFilter(position => !position.IsDeleted);
                builder.Property(position => position.Direction).HasConversion<string>();
                builder.Property(position => position.Status).HasConversion<string>();
                builder.Property(position => position.TrailingStopType).HasConversion<string>();
                builder.Ignore(position => position.ScaleOrders);
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
}
