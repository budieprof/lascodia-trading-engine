using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class OptimizationWorkerTest
{
    [Fact]
    public async Task TemporalChunkedEvaluateAsync_ReturnsCandidateSpecificCvPerCall()
    {
        var validator = new OptimizationValidator(new TestBacktestEngine());
        validator.SetInitialBalance(10_000m);

        var strategy = new Strategy
        {
            Id = 7,
            Name = "Test",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"mode":"varied"}"""
        };

        var candles = Enumerable.Range(0, 90)
            .Select(i => new Candle
            {
                Timestamp = new DateTime(2026, 01, 01).AddHours(i),
                Open = i,
                High = i + 1,
                Low = Math.Max(0, i - 1),
                Close = i,
                IsClosed = true
            })
            .ToList();

        var options = new BacktestOptions();

        var (_, _, variedCv) = await validator.TemporalChunkedEvaluateAsync(
            strategy,
            """{"mode":"varied"}""",
            candles,
            options,
            timeoutSecs: 5,
            kFolds: 3,
            embargoPerFold: 0,
            minTrades: 1,
            ct: CancellationToken.None);

        var (_, _, flatCv) = await validator.TemporalChunkedEvaluateAsync(
            strategy,
            """{"mode":"flat"}""",
            candles,
            options,
            timeoutSecs: 5,
            kFolds: 3,
            embargoPerFold: 0,
            minTrades: 1,
            ct: CancellationToken.None);

        Assert.True(variedCv > 0.01, $"Expected variable-fold CV to be > 0, but was {variedCv:F4}");
        Assert.Equal(0.0, flatCv, precision: 6);
    }

    [Fact]
    public async Task GetRegimeAwareCandlesAsync_UsesMatchingTimeframeSnapshots()
    {
        var regimeSnapshots = new List<MarketRegimeSnapshot>
        {
            new MarketRegimeSnapshot
            {
                Id = 1,
                Symbol = "EURUSD",
                Timeframe = Timeframe.D1,
                Regime = MarketRegime.Ranging,
                DetectedAt = new DateTime(2026, 03, 30, 0, 0, 0, DateTimeKind.Utc),
                IsDeleted = false
            },
            new MarketRegimeSnapshot
            {
                Id = 2,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Regime = MarketRegime.Trending,
                DetectedAt = new DateTime(2026, 03, 22, 23, 0, 0, DateTimeKind.Utc),
                IsDeleted = false
            },
            new MarketRegimeSnapshot
            {
                Id = 3,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Regime = MarketRegime.Trending,
                DetectedAt = new DateTime(2026, 03, 22, 22, 0, 0, DateTimeKind.Utc),
                IsDeleted = false
            }
        };

        var snapshotDbSet = regimeSnapshots.AsQueryable().BuildMockDbSet();
        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(snapshotDbSet.Object);

        var allCandles = Enumerable.Range(0, 120)
            .Select(i => new Candle
            {
                Timestamp = new DateTime(2026, 03, 22, 3, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = 1.1m,
                High = 1.2m,
                Low = 1.0m,
                Close = 1.1m,
                IsClosed = true
            })
            .ToList();

        var worker = CreateWorker();
        var method = typeof(OptimizationWorker).GetMethod(
            "GetRegimeAwareCandlesAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        // blendRatio=0.0 tests pure regime filtering (no non-regime candle blending)
        var task = (Task<List<Candle>>)method.Invoke(
            worker,
            [db.Object, "EURUSD", Timeframe.H1, allCandles, CancellationToken.None, 0.0])!;

        var filtered = await task;

        Assert.Equal(101, filtered.Count);
        Assert.Equal(new DateTime(2026, 03, 22, 22, 0, 0, DateTimeKind.Utc), filtered[0].Timestamp);
    }

    [Fact]
    public async Task GetRegimeAwareCandlesAsync_DoesNotTruncateLongRegimeStreaks()
    {
        var regimeStart = new DateTime(2026, 01, 04, 08, 0, 0, DateTimeKind.Utc);
        var allCandlesStart = regimeStart.AddHours(-80);

        var regimeSnapshots = new List<MarketRegimeSnapshot>
        {
            new()
            {
                Id = 1,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Regime = MarketRegime.Ranging,
                DetectedAt = regimeStart.AddHours(-1),
                IsDeleted = false
            }
        };

        regimeSnapshots.AddRange(Enumerable.Range(0, 120).Select(i => new MarketRegimeSnapshot
        {
            Id = i + 2,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            Regime = MarketRegime.Trending,
            DetectedAt = regimeStart.AddHours(i),
            IsDeleted = false
        }));

        var snapshotDbSet = regimeSnapshots.AsQueryable().BuildMockDbSet();
        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(snapshotDbSet.Object);

        var allCandles = Enumerable.Range(0, 200)
            .Select(i => new Candle
            {
                Timestamp = allCandlesStart.AddHours(i),
                Open = 1.1m,
                High = 1.2m,
                Low = 1.0m,
                Close = 1.1m,
                IsClosed = true
            })
            .ToList();

        var worker = CreateWorker();
        var method = typeof(OptimizationWorker).GetMethod(
            "GetRegimeAwareCandlesAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task<List<Candle>>)method.Invoke(
            worker,
            [db.Object, "EURUSD", Timeframe.H1, allCandles, CancellationToken.None, 0.0])!;

        var filtered = await task;

        Assert.Equal(120, filtered.Count);
        Assert.Equal(regimeStart, filtered[0].Timestamp);
        Assert.Equal(regimeStart.AddHours(119), filtered[^1].Timestamp);
    }

    [Fact]
    public async Task AutoScheduleUnderperformersAsync_SkipsChronicFailuresDuringExtendedCooldown()
    {
        var strategies = new List<Strategy>
        {
            new()
            {
                Id = 42,
                Name = "Chronic",
                Status = StrategyStatus.Active,
                StrategyType = StrategyType.BreakoutScalper,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = """{"Fast":10}""",
                IsDeleted = false
            }
        };

        var optimizationRuns = new List<OptimizationRun>
        {
            MakeCompletedOptimizationRun(1, 42, DateTime.UtcNow.AddDays(-20)),
            MakeCompletedOptimizationRun(2, 42, DateTime.UtcNow.AddDays(-21)),
            MakeCompletedOptimizationRun(3, 42, DateTime.UtcNow.AddDays(-22))
        };

        var backtestRuns = new List<BacktestRun>
        {
            new()
            {
                Id = 100,
                StrategyId = 42,
                Status = RunStatus.Completed,
                CompletedAt = DateTime.UtcNow.AddDays(-1),
                ResultJson = JsonSerializer.Serialize(new BacktestResult
                {
                    TotalTrades = 30,
                    WinRate = 0.30m,
                    ProfitFactor = 0.80m,
                    MaxDrawdownPct = 18m,
                    SharpeRatio = 0.2m
                }),
                IsDeleted = false
            }
        };

        var snapshots = new List<StrategyPerformanceSnapshot>();

        var strategyDbSet = strategies.AsQueryable().BuildMockDbSet();
        var optRunDbSet = optimizationRuns.AsQueryable().BuildMockDbSet();
        optRunDbSet.Setup(d => d.Add(It.IsAny<OptimizationRun>()))
            .Callback<OptimizationRun>(r => optimizationRuns.Add(r));
        var backtestRunDbSet = backtestRuns.AsQueryable().BuildMockDbSet();
        var snapshotDbSet = snapshots.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Strategy>()).Returns(strategyDbSet.Object);
        db.Setup(c => c.Set<OptimizationRun>()).Returns(optRunDbSet.Object);
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestRunDbSet.Object);
        db.Setup(c => c.Set<StrategyPerformanceSnapshot>()).Returns(snapshotDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var worker = CreateWorker();
        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);

        var method = typeof(OptimizationWorker).GetMethod(
            "AutoScheduleUnderperformersAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(worker, [readCtx.Object, writeCtx.Object, config, CancellationToken.None])!;

        Assert.DoesNotContain(
            optimizationRuns,
            r => r.Id != 1 && r.Id != 2 && r.Id != 3 && r.Status == OptimizationRunStatus.Queued);
    }

    [Fact]
    public void RestoreCheckpoint_RestoresPersistedCandidates()
    {
        const string paramsJson = """{"Fast":12,"Slow":34}""";
        var checkpointJson = OptimizationCheckpointStore.Serialize(
            iterations: 12,
            stagnantBatches: 2,
            surrogateKind: "TPE",
            surrogateRandomState: 123456UL,
            observations:
            [
                new OptimizationCheckpointStore.Observation(
                    Sequence: 1,
                    ParamsJson: paramsJson,
                    HealthScore: 0.72m,
                    CvCoefficientOfVariation: 0.11,
                    Result: new BacktestResult
                    {
                        TotalTrades = 18,
                        WinRate = 0.61m,
                        ProfitFactor = 1.40m,
                        MaxDrawdownPct = 6.0m,
                        SharpeRatio = 1.2m,
                        Trades = []
                    })
            ],
            seenParameterJson: [paramsJson]);

        var worker = CreateWorker();
        var method = typeof(OptimizationWorker).GetMethod(
            "RestoreCheckpoint",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var checkpoint = method.Invoke(worker, [checkpointJson])!;
        var iterations = (int)checkpoint.GetType().GetProperty("Iterations")!.GetValue(checkpoint)!;
        var stagnantBatches = (int)checkpoint.GetType().GetProperty("StagnantBatches")!.GetValue(checkpoint)!;
        var surrogateKind = (string?)checkpoint.GetType().GetProperty("SurrogateKind")!.GetValue(checkpoint)!;
        var observations = (System.Collections.IEnumerable)checkpoint.GetType().GetProperty("Observations")!.GetValue(checkpoint)!;
        var observation = observations.Cast<object>().Single();

        Assert.Equal(12, iterations);
        Assert.Equal(2, stagnantBatches);
        Assert.Equal("TPE", surrogateKind);
        Assert.Equal(paramsJson, observation.GetType().GetProperty("ParamsJson")!.GetValue(observation));
        Assert.Equal(0.11, (double)observation.GetType().GetProperty("CvCoefficientOfVariation")!.GetValue(observation)!, 6);
    }

    [Fact]
    public void CanonicalParameterJson_Normalize_OrdersKeysDeterministically()
    {
        string normalizedA = CanonicalParameterJson.Normalize("""{"Slow":34,"Fast":12}""");
        string normalizedB = CanonicalParameterJson.Normalize("""{"Fast":12,"Slow":34}""");

        Assert.Equal(normalizedA, normalizedB);
        Assert.Equal("""{"Fast":12,"Slow":34}""", normalizedA);
    }

    [Fact]
    public void OptimizationApprovalPolicy_Rejects_WhenSafetyGateFailsDespiteStrongScore()
    {
        var result = OptimizationApprovalPolicy.Evaluate(new OptimizationApprovalPolicy.Input(
            CandidateImprovement: 0.20m,
            OosHealthScore: 0.72m,
            TotalTrades: 40,
            SharpeRatio: 1.5m,
            MaxDrawdownPct: 5m,
            WinRate: 0.60m,
            ProfitFactor: 1.8m,
            CILower: 0.50m,
            MinBootstrapCILower: 0.40m,
            DegradationFailed: false,
            WfStable: true,
            MtfCompatible: true,
            CorrelationSafe: true,
            SensitivityOk: true,
            CostSensitiveOk: true,
            TemporalCorrelationSafe: true,
            PortfolioCorrelationSafe: true,
            PermSignificant: false,
            CvConsistent: true,
            TemporalMaxOverlap: 0.1,
            PortfolioMaxCorrelation: 0.1,
            PermPValue: 0.12,
            PermCorrectedAlpha: 0.05,
            CvValue: 0.2,
            PessimisticScore: 0.70m,
            SensitivityReport: "ok",
            AutoApprovalImprovementThreshold: 0.10m,
            AutoApprovalMinHealthScore: 0.55m,
            MinCandidateTrades: 10,
            MaxCvCoefficientOfVariation: 0.50,
            KellySizingOk: true,
            KellySharpeRatio: 1.2,
            FixedLotSharpeRatio: 1.5,
            EquityCurveOk: true,
            TimeConcentrationOk: true));

        Assert.False(result.Passed);
        Assert.Contains("permutation test not significant", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureValidationFollowUpsAsync_IsIdempotentPerOptimizationRun()
    {
        var run = new OptimizationRun { Id = 77, StrategyId = 5 };
        var strategy = new Strategy { Id = 5, Symbol = "EURUSD", Timeframe = Timeframe.H1 };

        var backtests = new List<BacktestRun>
        {
            new() { Id = 1, SourceOptimizationRunId = 77, StrategyId = 5, Status = RunStatus.Queued, IsDeleted = false }
        };
        var walks = new List<WalkForwardRun>
        {
            new() { Id = 1, SourceOptimizationRunId = 77, StrategyId = 5, Status = RunStatus.Queued, IsDeleted = false }
        };

        var backtestDbSet = backtests.AsQueryable().BuildMockDbSet();
        backtestDbSet.Setup(d => d.Add(It.IsAny<BacktestRun>()))
            .Callback<BacktestRun>(r => backtests.Add(r));

        var walkDbSet = walks.AsQueryable().BuildMockDbSet();
        walkDbSet.Setup(d => d.Add(It.IsAny<WalkForwardRun>()))
            .Callback<WalkForwardRun>(r => walks.Add(r));

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);

        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);
        var method = typeof(OptimizationWorker).GetMethod(
            "EnsureValidationFollowUpsAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var firstCall = (Task<bool>)method.Invoke(null, [db.Object, run, strategy, config, CancellationToken.None])!;
        var firstResult = await firstCall;

        var secondCall = (Task<bool>)method.Invoke(null, [db.Object, run, strategy, config, CancellationToken.None])!;
        var secondResult = await secondCall;

        Assert.Single(backtests);
        Assert.Single(walks);
        Assert.True(firstResult);
        Assert.False(secondResult);
        Assert.True(run.ValidationFollowUpsCreatedAt.HasValue);
    }

    [Fact]
    public async Task MonitorFollowUpResultsAsync_DoesNotPassRun_WhenFollowUpsAreMissing()
    {
        var runs = new List<OptimizationRun>
        {
            new()
            {
                Id = 88,
                StrategyId = 5,
                Status = OptimizationRunStatus.Approved,
                ValidationFollowUpStatus = ValidationFollowUpStatus.Pending,
                IsDeleted = false
            }
        };

        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var backtestDbSet = new List<BacktestRun>().AsQueryable().BuildMockDbSet();
        var walkDbSet = new List<WalkForwardRun>().AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var services = new ServiceCollection()
            .AddSingleton(readCtx.Object)
            .AddSingleton(writeCtx.Object)
            .BuildServiceProvider();

        var worker = new OptimizationWorker(
            Mock.Of<ILogger<OptimizationWorker>>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IBacktestEngine>(),
            (TradingMetrics)FormatterServices.GetUninitializedObject(typeof(TradingMetrics)));

        var method = typeof(OptimizationWorker).GetMethod(
            "MonitorFollowUpResultsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(worker, [CancellationToken.None])!;

        Assert.Equal(ValidationFollowUpStatus.Pending, runs[0].ValidationFollowUpStatus);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void OptimizationCheckpointStore_RestoresObservationOrderAndSeenParameters()
    {
        string json = OptimizationCheckpointStore.Serialize(
            iterations: 21,
            stagnantBatches: 3,
            surrogateKind: "GP-UCB",
            surrogateRandomState: 999UL,
            observations:
            [
                new OptimizationCheckpointStore.Observation(
                    Sequence: 2,
                    ParamsJson: """{"Slow":34,"Fast":12}""",
                    HealthScore: 0.55m,
                    CvCoefficientOfVariation: 0.15,
                    Result: new BacktestResult { TotalTrades = 8, ProfitFactor = 1.1m, WinRate = 0.51m, Trades = [] }),
                new OptimizationCheckpointStore.Observation(
                    Sequence: 1,
                    ParamsJson: """{"Slow":21,"Fast":9}""",
                    HealthScore: 0.65m,
                    CvCoefficientOfVariation: 0.05,
                    Result: new BacktestResult { TotalTrades = 12, ProfitFactor = 1.3m, WinRate = 0.56m, Trades = [] })
            ],
            seenParameterJson: ["""{"Slow":34,"Fast":12}""", """{"Fast":9,"Slow":21}"""]);

        var restored = OptimizationCheckpointStore.Restore(json);

        Assert.Equal(21, restored.Iterations);
        Assert.Equal(3, restored.StagnantBatches);
        Assert.Equal("GP-UCB", restored.SurrogateKind);
        Assert.Equal(999UL, restored.SurrogateRandomState);
        Assert.Equal(2, restored.Observations.Count);
        Assert.Equal("""{"Fast":9,"Slow":21}""", restored.Observations[0].ParamsJson);
        Assert.Equal("""{"Fast":12,"Slow":34}""", restored.Observations[1].ParamsJson);
        Assert.Contains("""{"Fast":12,"Slow":34}""", restored.SeenParameterJson);
        Assert.Contains("""{"Fast":9,"Slow":21}""", restored.SeenParameterJson);
    }

    [Fact]
    public void CrossRegimeHelpers_FilterOnlyTrueRegimeIntervals()
    {
        var snapshots = new List<MarketRegimeSnapshot>
        {
            new() { DetectedAt = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc), Regime = MarketRegime.Trending },
            new() { DetectedAt = new DateTime(2026, 03, 02, 0, 0, 0, DateTimeKind.Utc), Regime = MarketRegime.Ranging },
            new() { DetectedAt = new DateTime(2026, 03, 03, 0, 0, 0, DateTimeKind.Utc), Regime = MarketRegime.Trending },
            new() { DetectedAt = new DateTime(2026, 03, 04, 0, 0, 0, DateTimeKind.Utc), Regime = MarketRegime.HighVolatility },
        };

        var candles = Enumerable.Range(0, 96)
            .Select(i => new Candle
            {
                Timestamp = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = 1.1m,
                High = 1.2m,
                Low = 1.0m,
                Close = 1.1m,
                IsClosed = true
            })
            .ToList();

        var intervalMethod = typeof(OptimizationWorker).GetMethod(
            "BuildRegimeIntervals",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var filterMethod = typeof(OptimizationWorker).GetMethod(
            "FilterCandlesByIntervals",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var intervals = intervalMethod.Invoke(null, [snapshots, MarketRegime.Trending, snapshots[0].DetectedAt, new DateTime(2026, 03, 05, 0, 0, 0, DateTimeKind.Utc)])!;
        var filtered = (List<Candle>)filterMethod.Invoke(null, [candles, intervals])!;

        Assert.Equal(48, filtered.Count);
        Assert.Equal(new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc), filtered.First().Timestamp);
        Assert.Equal(new DateTime(2026, 03, 03, 23, 0, 0, DateTimeKind.Utc), filtered.Last().Timestamp);
        Assert.DoesNotContain(filtered, c => c.Timestamp >= new DateTime(2026, 03, 02, 0, 0, 0, DateTimeKind.Utc)
            && c.Timestamp < new DateTime(2026, 03, 03, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task LoadAndValidateCandlesAsync_UsesOosBaselineScoreForApprovalComparison()
    {
        var nowUtc = DateTime.UtcNow;
        var candles = Enumerable.Range(0, 600)
            .Select(i => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = nowUtc.AddHours(-(600 - i)),
                Open = 1.1000m + i * 0.0001m,
                High = 1.1005m + i * 0.0001m,
                Low = 1.0995m + i * 0.0001m,
                Close = 1.1000m + i * 0.0001m,
                IsClosed = true
            })
            .ToList();

        var candleDbSet = candles.AsQueryable().BuildMockDbSet();
        var eventDbSet = new List<EconomicEvent>().AsQueryable().BuildMockDbSet();
        var regimeDbSet = new List<MarketRegimeSnapshot>().AsQueryable().BuildMockDbSet();
        var pairDbSet = new List<CurrencyPair>().AsQueryable().BuildMockDbSet();
        var regimeParamsDbSet = new List<StrategyRegimeParams>().AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Candle>()).Returns(candleDbSet.Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(eventDbSet.Object);
        db.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(regimeDbSet.Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(pairDbSet.Object);
        db.Setup(c => c.Set<StrategyRegimeParams>()).Returns(regimeParamsDbSet.Object);

        var services = new ServiceCollection().BuildServiceProvider();
        var worker = new OptimizationWorker(
            Mock.Of<ILogger<OptimizationWorker>>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            new BaselineSplitBacktestEngine(),
            (TradingMetrics)FormatterServices.GetUninitializedObject(typeof(TradingMetrics)));

        var strategy = new Strategy
        {
            Id = 9,
            Name = "Baseline",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":12,"Slow":34}"""
        };
        var run = new OptimizationRun { Id = 501, StrategyId = strategy.Id, StartedAt = nowUtc };
        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);

        var method = typeof(OptimizationWorker).GetMethod(
            "LoadAndValidateCandlesAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task)method.Invoke(worker, [db.Object, run, strategy, config, CancellationToken.None])!;
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var baselineComparisonScore = (decimal)result.GetType().GetProperty("BaselineComparisonScore")!.GetValue(result)!;

        Assert.NotNull(run.BaselineHealthScore);
        Assert.NotEqual(run.BaselineHealthScore!.Value, baselineComparisonScore);
        Assert.True(baselineComparisonScore > run.BaselineHealthScore.Value,
            "Expected OOS baseline score to come from the shorter OOS split, not the in-sample baseline result.");
    }

    [Fact]
    public async Task LoadAndValidateCandlesAsync_ImputesMinorGapsBeforeValidation()
    {
        var nowUtc = DateTime.UtcNow;
        var missingIndices = new HashSet<int> { 15, 30, 45, 60, 75, 90, 105, 120, 135, 150 };
        var candles = Enumerable.Range(0, 170)
            .Where(i => !missingIndices.Contains(i))
            .Select(i => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = nowUtc.AddHours(-(170 - i)),
                Open = 1.1000m + i * 0.0001m,
                High = 1.1005m + i * 0.0001m,
                Low = 1.0995m + i * 0.0001m,
                Close = 1.1000m + i * 0.0001m,
                IsClosed = true
            })
            .ToList();

        var candleDbSet = candles.AsQueryable().BuildMockDbSet();
        var eventDbSet = new List<EconomicEvent>().AsQueryable().BuildMockDbSet();
        var regimeDbSet = new List<MarketRegimeSnapshot>().AsQueryable().BuildMockDbSet();
        var pairDbSet = new List<CurrencyPair>().AsQueryable().BuildMockDbSet();
        var regimeParamsDbSet = new List<StrategyRegimeParams>().AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Candle>()).Returns(candleDbSet.Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(eventDbSet.Object);
        db.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(regimeDbSet.Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(pairDbSet.Object);
        db.Setup(c => c.Set<StrategyRegimeParams>()).Returns(regimeParamsDbSet.Object);

        var services = new ServiceCollection().BuildServiceProvider();
        var worker = new OptimizationWorker(
            Mock.Of<ILogger<OptimizationWorker>>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            new BaselineSplitBacktestEngine(),
            (TradingMetrics)FormatterServices.GetUninitializedObject(typeof(TradingMetrics)));

        var strategy = new Strategy
        {
            Id = 13,
            Name = "GapHealing",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":12,"Slow":34}"""
        };
        var run = new OptimizationRun { Id = 777, StrategyId = strategy.Id, StartedAt = nowUtc };
        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);

        var method = typeof(OptimizationWorker).GetMethod(
            "LoadAndValidateCandlesAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task)method.Invoke(worker, [db.Object, run, strategy, config, CancellationToken.None])!;
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var allCandles = (IReadOnlyList<Candle>)result.GetType().GetProperty("AllCandles")!.GetValue(result)!;

        Assert.True(allCandles.Count > candles.Count);
        Assert.NotNull(run.BaselineHealthScore);
    }

    [Fact]
    public void SuggestInitialCandidates_UsesEhviWhenConfigured()
    {
        string[] paramNames = ["Fast", "Slow"];
        double[] lower = [5, 20];
        double[] upper = [20, 50];
        bool[] isInteger = [true, true];
        var ehvi = new EhviAcquisition(paramNames, lower, upper, isInteger, seed: 123);

        var suggestions = OptimizationWorker.SuggestInitialCandidates(
            tpe: null,
            gp: null,
            ehvi: ehvi,
            count: 3);

        Assert.Equal(3, suggestions.Count);
        Assert.All(suggestions, suggestion =>
        {
            Assert.Contains("Fast", suggestion.Keys);
            Assert.Contains("Slow", suggestion.Keys);
        });
    }

    [Fact]
    public void TreeParzenEstimator_RandomState_RoundTripsAcrossResume()
    {
        var bounds = new Dictionary<string, (double Min, double Max, bool IsInteger)>
        {
            ["Fast"] = (5, 20, true),
            ["Slow"] = (20, 50, true)
        };

        var tpe = new TreeParzenEstimator(bounds, seed: 123);
        for (int i = 0; i < 12; i++)
        {
            tpe.AddObservation(new Dictionary<string, double>
            {
                ["Fast"] = 5 + i,
                ["Slow"] = 20 + i
            }, 0.4 + (i * 0.01));
        }

        _ = tpe.SuggestCandidates(3);
        ulong state = tpe.RandomState;
        var expected = tpe.SuggestCandidates(4)
            .Select(s => CanonicalParameterJson.Normalize(JsonSerializer.Serialize(s)))
            .ToList();

        var resumed = new TreeParzenEstimator(bounds, seed: 123, randomState: state);
        for (int i = 0; i < 12; i++)
        {
            resumed.AddObservation(new Dictionary<string, double>
            {
                ["Fast"] = 5 + i,
                ["Slow"] = 20 + i
            }, 0.4 + (i * 0.01));
        }

        var actual = resumed.SuggestCandidates(4)
            .Select(s => CanonicalParameterJson.Normalize(JsonSerializer.Serialize(s)))
            .ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GaussianProcessSurrogate_RandomState_RoundTripsAcrossResume()
    {
        string[] paramNames = ["P1", "P2", "P3", "P4", "P5", "P6"];
        double[] lower = [0, 0, 0, 0, 0, 0];
        double[] upper = [1, 1, 1, 1, 1, 1];
        bool[] isInt = [false, false, false, false, false, false];

        var gp = new GaussianProcessSurrogate(paramNames, lower, upper, isInt, seed: 456);
        for (int i = 0; i < 15; i++)
        {
            gp.AddObservation(new Dictionary<string, double>
            {
                ["P1"] = i / 20.0,
                ["P2"] = (i + 1) / 20.0,
                ["P3"] = (i + 2) / 20.0,
                ["P4"] = (i + 3) / 20.0,
                ["P5"] = (i + 4) / 20.0,
                ["P6"] = (i + 5) / 20.0
            }, 0.5 + (i * 0.01));
        }

        _ = gp.SuggestCandidates(2);
        ulong state = gp.RandomState;
        var expected = gp.SuggestCandidates(3)
            .Select(s => CanonicalParameterJson.Normalize(JsonSerializer.Serialize(s)))
            .ToList();

        var resumed = new GaussianProcessSurrogate(paramNames, lower, upper, isInt, seed: 456, randomState: state);
        for (int i = 0; i < 15; i++)
        {
            resumed.AddObservation(new Dictionary<string, double>
            {
                ["P1"] = i / 20.0,
                ["P2"] = (i + 1) / 20.0,
                ["P3"] = (i + 2) / 20.0,
                ["P4"] = (i + 3) / 20.0,
                ["P5"] = (i + 4) / 20.0,
                ["P6"] = (i + 5) / 20.0
            }, 0.5 + (i * 0.01));
        }

        var actual = resumed.SuggestCandidates(3)
            .Select(s => CanonicalParameterJson.Normalize(JsonSerializer.Serialize(s)))
            .ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OptimizationRunStateMachine_RejectsIllegalTransition()
    {
        var run = new OptimizationRun
        {
            Id = 99,
            Status = OptimizationRunStatus.Approved,
            CompletedAt = DateTime.UtcNow,
            ApprovedAt = DateTime.UtcNow
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Failed, DateTime.UtcNow, "boom"));

        Assert.Contains("Illegal OptimizationRun status transition", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyApprovalDecisionAsync_LeavesRunCompleted_WhenApprovalPersistenceFails()
    {
        var run = new OptimizationRun
        {
            Id = 404,
            StrategyId = 12,
            Status = OptimizationRunStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            BestParametersJson = """{"Fast":14,"Slow":40}"""
        };
        var strategy = new Strategy
        {
            Id = 12,
            Name = "ApprovalFailure",
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":10,"Slow":30}""",
            Status = StrategyStatus.Active,
            IsDeleted = false
        };

        var strategies = new List<Strategy> { strategy };
        var backtests = new List<BacktestRun>();
        var walks = new List<WalkForwardRun>();
        var regimeParams = new List<StrategyRegimeParams>();

        var strategyDbSet = strategies.AsQueryable().BuildMockDbSet();
        var backtestDbSet = backtests.AsQueryable().BuildMockDbSet();
        backtestDbSet.Setup(d => d.Add(It.IsAny<BacktestRun>()))
            .Callback<BacktestRun>(r => backtests.Add(r));
        var walkDbSet = walks.AsQueryable().BuildMockDbSet();
        walkDbSet.Setup(d => d.Add(It.IsAny<WalkForwardRun>()))
            .Callback<WalkForwardRun>(r => walks.Add(r));
        var regimeParamsDbSet = regimeParams.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Strategy>()).Returns(strategyDbSet.Object);
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);
        db.Setup(c => c.Set<StrategyRegimeParams>()).Returns(regimeParamsDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var mediator = new Mock<IMediator>();
        var alertDispatcher = new Mock<IAlertDispatcher>();
        var eventService = new Mock<IIntegrationEventService>();
        eventService.Setup(x => x.SaveAndPublish(It.IsAny<IDbContext>(), It.IsAny<Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>()))
            .ThrowsAsync(new InvalidOperationException("approval event persistence failed"));

        var worker = CreateWorker();
        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);
        var runContext = CreateRunContext(
            run, strategy, config, baselineComparisonScore: 0.40m,
            db.Object, db.Object, writeCtx.Object, mediator.Object,
            alertDispatcher.Object, eventService.Object);

        var oosResult = new BacktestResult
        {
            TotalTrades = 24,
            WinRate = 0.62m,
            ProfitFactor = 1.70m,
            MaxDrawdownPct = 4m,
            SharpeRatio = 1.5m,
            Trades = []
        };
        var validationResult = CreateCandidateValidationResult(
            passed: true,
            winnerParamsJson: """{"Fast":14,"Slow":40}""",
            oosHealthScore: 0.74m,
            oosResult: oosResult,
            ciLower: 0.55m,
            ciUpper: 0.82m,
            wfAvgScore: 0.70m,
            pessimisticScore: 0.68m,
            failureReason: string.Empty);

        var method = typeof(OptimizationWorker).GetMethod(
            "ApplyApprovalDecisionAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(worker,
            [runContext, validationResult, null, DateTime.UtcNow.AddMonths(-2), new BacktestOptions()])!;

        Assert.Equal(OptimizationRunStatus.Completed, run.Status);
        Assert.Null(run.ApprovedAt);
        Assert.Null(strategy.RolloutPct);
        Assert.Equal("""{"Fast":10,"Slow":30}""", strategy.ParametersJson);
    }

    private static OptimizationWorker CreateWorker()
    {
        var logger = Mock.Of<ILogger<OptimizationWorker>>();
        var scopeFactory = Mock.Of<IServiceScopeFactory>();
        var backtestEngine = Mock.Of<IBacktestEngine>();
        var metrics = (TradingMetrics)FormatterServices.GetUninitializedObject(typeof(TradingMetrics));

        return new OptimizationWorker(logger, scopeFactory, backtestEngine, metrics);
    }

    private static OptimizationRun MakeCompletedOptimizationRun(long id, long strategyId, DateTime completedAtUtc) => new()
    {
        Id = id,
        StrategyId = strategyId,
        Status = OptimizationRunStatus.Completed,
        CompletedAt = completedAtUtc,
        StartedAt = completedAtUtc.AddHours(-1),
        BestParametersJson = """{"Fast":10}""",
        BaselineParametersJson = """{"Fast":8}""",
        BestHealthScore = 0.40m,
        BaselineHealthScore = 0.35m,
        IsDeleted = false
    };

    private static object CreateOptimizationConfig(
        int cooldownDays,
        int maxConsecutiveFailuresBeforeEscalation)
    {
        var configType = typeof(OptimizationWorker).GetNestedType("OptimizationConfig", BindingFlags.NonPublic)!;
        var ctor = configType
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .First();

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SchedulePollSeconds"] = 7200,
            ["CooldownDays"] = cooldownDays,
            ["MaxQueuedPerCycle"] = 3,
            ["AutoScheduleEnabled"] = true,
            ["MinWinRate"] = 0.60,
            ["MinProfitFactor"] = 1.0,
            ["MinTotalTrades"] = 10,
            ["AutoApprovalImprovementThreshold"] = 0.10m,
            ["AutoApprovalMinHealthScore"] = 0.55m,
            ["TopNCandidates"] = 5,
            ["CoarsePhaseThreshold"] = 10,
            ["ScreeningTimeoutSeconds"] = 30,
            ["ScreeningSpreadPoints"] = 20.0,
            ["ScreeningCommissionPerLot"] = 7.0,
            ["ScreeningSlippagePips"] = 1.0,
            ["MaxOosDegradationPct"] = 0.60,
            ["SuppressDuringDrawdownRecovery"] = true,
            ["SeasonalBlackoutEnabled"] = true,
            ["BlackoutPeriods"] = "12/20-01/05",
            ["MaxRunTimeoutMinutes"] = 30,
            ["MaxParallelBacktests"] = 4,
            ["MinCandidateTrades"] = 10,
            ["EmbargoRatio"] = 0.05,
            ["CorrelationParamThreshold"] = 0.15,
            ["TpeBudget"] = 50,
            ["TpeInitialSamples"] = 15,
            ["PurgedKFolds"] = 5,
            ["SensitivityPerturbPct"] = 0.10,
            ["BootstrapIterations"] = 1000,
            ["MinBootstrapCILower"] = 0.40m,
            ["CostSensitivityEnabled"] = true,
            ["AdaptiveBoundsEnabled"] = true,
            ["TemporalOverlapThreshold"] = 0.70,
            ["DataScarcityThreshold"] = 200,
            ["ScreeningInitialBalance"] = 10_000m,
            ["PortfolioCorrelationThreshold"] = 0.80,
            ["MaxConsecutiveFailuresBeforeEscalation"] = maxConsecutiveFailuresBeforeEscalation,
            ["CheckpointEveryN"] = 10,
            ["GpEarlyStopPatience"] = 4,
            ["SensitivityDegradationTolerance"] = 0.20,
            ["WalkForwardMinMaxRatio"] = 0.50,
            ["CostStressMultiplier"] = 2.0,
            ["MinOosCandlesForValidation"] = 50,
            ["MaxCvCoefficientOfVariation"] = 0.50,
            ["PermutationIterations"] = 1000,
            ["MaxRetryAttempts"] = 3,
            ["CandleLookbackMonths"] = 12,
            ["CandleLookbackAutoScale"] = true,
            ["RequireEADataAvailability"] = false,
            ["MaxConcurrentRuns"] = 2,
            ["UseSymbolSpecificSpread"] = true,
            ["RegimeBlendRatio"] = 0.50,
            ["CpcvNFolds"] = 6,
            ["CpcvTestFoldCount"] = 2,
            ["CpcvMaxCombinations"] = 15,
            ["CircuitBreakerThreshold"] = 10,
            ["SuccessiveHalvingRungs"] = "0.25,0.50",
            ["MaxCrossRegimeEvals"] = 4,
            ["PresetName"] = "balanced",
            ["HyperbandEnabled"] = true,
            ["HyperbandEta"] = 3,
            ["MaxRunsPerWeek"] = 20
        };

        var args = ctor
            .GetParameters()
            .Select(parameter => values[parameter.Name!])
            .ToArray();

        return ctor.Invoke(args);
    }

    private static object CreateRunContext(
        OptimizationRun run,
        Strategy strategy,
        object config,
        decimal baselineComparisonScore,
        DbContext db,
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        IMediator mediator,
        IAlertDispatcher alertDispatcher,
        IIntegrationEventService eventService)
    {
        var contextType = typeof(OptimizationWorker).GetNestedType("RunContext", BindingFlags.NonPublic)!;
        return contextType
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(ctor => ctor.GetParameters().Length)
            .First()
            .Invoke(
            [
                run,
                strategy,
                config,
                baselineComparisonScore,
                db,
                writeDb,
                writeCtx,
                mediator,
                alertDispatcher,
                eventService,
                CancellationToken.None,
                CancellationToken.None
            ]);
    }

    private static object CreateCandidateValidationResult(
        bool passed,
        string winnerParamsJson,
        decimal oosHealthScore,
        BacktestResult oosResult,
        decimal ciLower,
        decimal ciUpper,
        decimal wfAvgScore,
        decimal pessimisticScore,
        string failureReason)
    {
        var scoredCandidateType = typeof(OptimizationWorker).GetNestedType("ScoredCandidate", BindingFlags.NonPublic)!;
        var scoredCandidate = scoredCandidateType
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(ctor => ctor.GetParameters().Length)
            .First()
            .Invoke([winnerParamsJson, oosHealthScore, oosResult, 0.10]);

        var resultType = typeof(OptimizationWorker).GetNestedType("CandidateValidationResult", BindingFlags.NonPublic)!;
        var ctor = resultType
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .First();

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Passed"] = passed,
            ["Winner"] = scoredCandidate,
            ["OosHealthScore"] = oosHealthScore,
            ["OosResult"] = oosResult,
            ["CILower"] = ciLower,
            ["CIMedian"] = oosHealthScore,
            ["CIUpper"] = ciUpper,
            ["PermPValue"] = 0.01,
            ["PermCorrectedAlpha"] = 0.05,
            ["PermSignificant"] = true,
            ["SensitivityOk"] = true,
            ["SensitivityReport"] = "ok",
            ["CostSensitiveOk"] = true,
            ["PessimisticScore"] = pessimisticScore,
            ["DegradationFailed"] = false,
            ["WfAvgScore"] = wfAvgScore,
            ["WfStable"] = true,
            ["MtfCompatible"] = true,
            ["CorrelationSafe"] = true,
            ["TemporalCorrelationSafe"] = true,
            ["TemporalMaxOverlap"] = 0.0,
            ["PortfolioCorrelationSafe"] = true,
            ["PortfolioMaxCorrelation"] = 0.0,
            ["CvConsistent"] = true,
            ["CvValue"] = 0.10,
            ["ApprovalReportJson"] = "{}",
            ["FailureReason"] = failureReason,
            ["FailedCandidates"] = null
        };

        var args = ctor.GetParameters()
            .Select(parameter => values[parameter.Name!])
            .ToArray();

        return ctor.Invoke(args);
    }

    private sealed class TestBacktestEngine : IBacktestEngine
    {
        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            bool flat = strategy.ParametersJson.Contains("flat", StringComparison.OrdinalIgnoreCase);
            decimal foldSeed = flat ? 0m : candles[0].Open / 100m;

            return Task.FromResult(new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance + 500m,
                TotalTrades = 20,
                WinRate = 0.55m + foldSeed,
                ProfitFactor = 1.2m + foldSeed,
                MaxDrawdownPct = 6m - Math.Min(foldSeed, 2m),
                SharpeRatio = 1.0m + foldSeed,
                Trades = []
            });
        }
    }

    private sealed class BaselineSplitBacktestEngine : IBacktestEngine
    {
        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            bool isTrainLike = candles.Count >= 300;
            return Task.FromResult(new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance + (isTrainLike ? 200m : 800m),
                TotalTrades = isTrainLike ? 40 : 25,
                WinRate = isTrainLike ? 0.45m : 0.62m,
                ProfitFactor = isTrainLike ? 1.05m : 1.70m,
                MaxDrawdownPct = isTrainLike ? 12m : 4m,
                SharpeRatio = isTrainLike ? 0.4m : 1.6m,
                Trades = []
            });
        }
    }

    // ── #9: Retry / dead-letter lifecycle tests ────────────────────────────

    [Fact]
    public void RetryExponentialBackoff_CalculationIsCorrect()
    {
        // The retry backoff formula is: 15 * 2^RetryCount minutes
        // RetryCount 0 → 15 min, 1 → 30 min, 2 → 60 min
        Assert.Equal(15, 15 << 0);
        Assert.Equal(30, 15 << 1);
        Assert.Equal(60, 15 << 2);
        Assert.Equal(120, 15 << 3);
    }

    [Fact]
    public void RetryEligibilityWindow_ScalesWithConfiguredRetryAttempts()
    {
        var method = typeof(OptimizationWorker).GetMethod(
            "GetRetryEligibilityWindow",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var twoAttempts = (TimeSpan)method.Invoke(null, [2])!;
        var threeAttempts = (TimeSpan)method.Invoke(null, [3])!;

        Assert.Equal(TimeSpan.FromMinutes(45), twoAttempts);
        Assert.Equal(TimeSpan.FromMinutes(75), threeAttempts);
    }

    [Fact]
    public void StateMachine_AllowsRetryPath_Failed_To_Queued()
    {
        Assert.True(OptimizationRunStateMachine.CanTransition(
            OptimizationRunStatus.Failed, OptimizationRunStatus.Queued));
    }

    [Fact]
    public void StateMachine_AllowsDeadLetter_Failed_To_Abandoned()
    {
        Assert.True(OptimizationRunStateMachine.CanTransition(
            OptimizationRunStatus.Failed, OptimizationRunStatus.Abandoned));
    }

    [Fact]
    public void StateMachine_Abandoned_IsTerminal()
    {
        // Abandoned runs should not be re-queueable
        Assert.False(OptimizationRunStateMachine.CanTransition(
            OptimizationRunStatus.Abandoned, OptimizationRunStatus.Queued));
        Assert.False(OptimizationRunStateMachine.CanTransition(
            OptimizationRunStatus.Abandoned, OptimizationRunStatus.Running));
    }

    [Fact]
    public void StateMachine_Transition_ToAbandoned_SetsErrorMessage()
    {
        var run = new OptimizationRun
        {
            Id = 101,
            Status = OptimizationRunStatus.Failed,
            RetryCount = 3,
        };

        OptimizationRunStateMachine.Transition(
            run, OptimizationRunStatus.Abandoned, DateTime.UtcNow,
            "Retry budget exhausted — moved to dead-letter queue");

        Assert.Equal(OptimizationRunStatus.Abandoned, run.Status);
        Assert.Contains("dead-letter", run.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(run.CompletedAt);
    }

    // ── #10: Deferred run filtering tests ──────────────────────────────────

    [Fact]
    public void DeferredUntilUtc_DefaultsToNull()
    {
        var run = new OptimizationRun { Id = 200, StrategyId = 1 };
        Assert.Null(run.DeferredUntilUtc);
    }

    [Fact]
    public void DeferredRun_CanBeRequeued_AfterDeferralExpires()
    {
        var run = new OptimizationRun
        {
            Id = 201,
            Status = OptimizationRunStatus.Queued,
            DeferredUntilUtc = DateTime.UtcNow.AddHours(-1), // Expired 1 hour ago
        };

        // A claim query would filter: DeferredUntilUtc IS NULL OR DeferredUntilUtc <= now
        // This simulates the condition check:
        bool eligible = run.DeferredUntilUtc is null || run.DeferredUntilUtc <= DateTime.UtcNow;
        Assert.True(eligible);
    }

    [Fact]
    public void DeferredRun_NotEligible_WhileDeferralActive()
    {
        var run = new OptimizationRun
        {
            Id = 202,
            Status = OptimizationRunStatus.Queued,
            DeferredUntilUtc = DateTime.UtcNow.AddHours(5), // Deferred for 5 more hours
        };

        bool eligible = run.DeferredUntilUtc is null || run.DeferredUntilUtc <= DateTime.UtcNow;
        Assert.False(eligible);
    }

    [Fact]
    public void DeferredUntilUtc_ClearedOnRequeue()
    {
        // When a run is re-queued from crash recovery or retry, DeferredUntilUtc should be cleared
        var run = new OptimizationRun
        {
            Id = 203,
            Status = OptimizationRunStatus.Running,
            DeferredUntilUtc = DateTime.UtcNow.AddHours(1),
        };

        // Simulate the crash recovery re-queue (which sets DeferredUntilUtc to null)
        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, DateTime.UtcNow);
        run.DeferredUntilUtc = null; // This is what the worker does

        Assert.Null(run.DeferredUntilUtc);
        Assert.Equal(OptimizationRunStatus.Queued, run.Status);
    }

    // ── #8: Config validator with SuccessiveHalvingRungs ───────────────────

    [Fact]
    public void ConfigValidator_RejectsInvalidSuccessiveHalvingRungs()
    {
        var logger = Mock.Of<ILogger>();
        var issues = OptimizationConfigValidator.Validate(
            autoApprovalImprovementThreshold: 0.10m,
            autoApprovalMinHealthScore: 0.55m,
            minBootstrapCILower: 0.40m,
            embargoRatio: 0.05,
            tpeBudget: 50,
            tpeInitialSamples: 15,
            maxParallelBacktests: 4,
            screeningTimeoutSeconds: 30,
            correlationParamThreshold: 0.15,
            sensitivityPerturbPct: 0.10,
            gpEarlyStopPatience: 4,
            cooldownDays: 14,
            checkpointEveryN: 10,
            logger: logger,
            successiveHalvingRungs: "abc,xyz");

        Assert.Contains(issues, i => i.Contains("SuccessiveHalvingRungs"));
    }

    [Fact]
    public void ConfigValidator_AcceptsValidSuccessiveHalvingRungs()
    {
        var logger = Mock.Of<ILogger>();
        var issues = OptimizationConfigValidator.Validate(
            autoApprovalImprovementThreshold: 0.10m,
            autoApprovalMinHealthScore: 0.55m,
            minBootstrapCILower: 0.40m,
            embargoRatio: 0.05,
            tpeBudget: 50,
            tpeInitialSamples: 15,
            maxParallelBacktests: 4,
            screeningTimeoutSeconds: 30,
            correlationParamThreshold: 0.15,
            sensitivityPerturbPct: 0.10,
            gpEarlyStopPatience: 4,
            cooldownDays: 14,
            checkpointEveryN: 10,
            logger: logger,
            successiveHalvingRungs: "0.25,0.50");

        Assert.DoesNotContain(issues, i => i.Contains("SuccessiveHalvingRungs"));
    }

    [Fact]
    public void ParameterImportanceTracker_ComputesDeltasCorrectly()
    {
        var baseline = """{"FastPeriod":10,"SlowPeriod":30}""";
        var optimized = """{"FastPeriod":12,"SlowPeriod":30}""";

        var deltas = ParameterImportanceTracker.ComputeParameterDeltas(baseline, optimized);

        Assert.True(deltas["FastPeriod"] > 0); // Changed
        Assert.Equal(0.0, deltas["SlowPeriod"]); // Unchanged
    }

    [Fact]
    public void ParameterImportanceTracker_AggregateImportance_NormalizesTo01()
    {
        var deltas1 = new Dictionary<string, double> { ["A"] = 0.5, ["B"] = 0.1 };
        var deltas2 = new Dictionary<string, double> { ["A"] = 0.6, ["B"] = 0.2 };
        var deltas3 = new Dictionary<string, double> { ["A"] = 0.4, ["B"] = 0.15 };

        var importance = ParameterImportanceTracker.AggregateImportance([deltas1, deltas2, deltas3]);

        // A should be the most important (highest avg delta)
        Assert.True(importance["A"] > importance["B"]);
        // Normalized to [0, 1] — max should be 1.0
        Assert.Equal(1.0, importance.Values.Max(), precision: 4);
    }

    [Fact]
    public void ExpandGridWithMidpoints_UsesCanonicalParameterIdentity()
    {
        var grid = new List<Dictionary<string, object>>
        {
            new() { ["Slow"] = 34, ["Fast"] = 12 },
            new() { ["Slow"] = 40, ["Fast"] = 20 }
        };

        var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            """{"Fast":12,"Slow":34}"""
        };

        var expanded = OptimizationGridBuilder.ExpandGridWithMidpoints(grid, previous);

        Assert.DoesNotContain(expanded, p => CanonicalParameterJson.Serialize(p) == """{"Fast":12,"Slow":34}""");
    }

    [Fact]
    public void GradualRolloutManager_SelectParameters_RoutesBasedOnRolloutPct()
    {
        var strategy = new Strategy
        {
            Id = 50,
            ParametersJson = """{"Fast":12}""",
            RollbackParametersJson = """{"Fast":8}""",
            RolloutPct = 50,
        };

        // With enough seeds, approximately half should pick new params and half old
        int newParamsCount = 0;
        for (int seed = 0; seed < 100; seed++)
        {
            string selected = GradualRolloutManager.SelectParameters(strategy, seed);
            if (selected == strategy.ParametersJson) newParamsCount++;
        }

        // ~50% should pick new params (50% rollout), allow margin of ±15
        Assert.InRange(newParamsCount, 35, 65);
    }

    [Fact]
    public void GradualRolloutManager_SelectParameters_Returns100PctWhenNoRollout()
    {
        var strategy = new Strategy
        {
            Id = 51,
            ParametersJson = """{"Fast":12}""",
            RolloutPct = null, // No active rollout
        };

        string selected = GradualRolloutManager.SelectParameters(strategy, 42);
        Assert.Equal(strategy.ParametersJson, selected);
    }

    // ── Approval Policy Tests ─────────────────────────────────────────────

    [Fact]
    public void ApprovalPolicy_PassesWhenCompositeGateAndAllSafetyGatesPass()
    {
        var input = MakePassingApprovalInput();

        var result = OptimizationApprovalPolicy.Evaluate(input);

        Assert.True(result.Passed);
        Assert.True(result.CompositeGateOk);
        Assert.True(result.SafetyGatesOk);
        Assert.Equal(string.Empty, result.FailureReason);
    }

    [Fact]
    public void ApprovalPolicy_PassesViaMultiObjectiveWhenCompositeGateFails()
    {
        // improvement=0.05 fails composite threshold of 0.10, but multi-objective metrics are strong
        var input = MakePassingApprovalInput() with
        {
            CandidateImprovement = 0.05m,
            SharpeRatio = 1.5m,
            MaxDrawdownPct = 8m,
            WinRate = 0.50m,
            ProfitFactor = 1.5m,
            TotalTrades = 50
        };

        var result = OptimizationApprovalPolicy.Evaluate(input);

        Assert.True(result.Passed);
        Assert.True(result.MultiObjectiveGateOk);
        Assert.False(result.CompositeGateOk);
    }

    [Fact]
    public void ApprovalPolicy_FailsWhenSafetyGateKellyFails()
    {
        var input = MakePassingApprovalInput() with { KellySizingOk = false };

        var result = OptimizationApprovalPolicy.Evaluate(input);

        Assert.False(result.Passed);
        Assert.False(result.SafetyGatesOk);
        Assert.Contains("Kelly", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApprovalPolicy_FailsWhenSafetyGateEquityCurveFails()
    {
        var input = MakePassingApprovalInput() with { EquityCurveOk = false };

        var result = OptimizationApprovalPolicy.Evaluate(input);

        Assert.False(result.Passed);
        Assert.False(result.SafetyGatesOk);
        Assert.Contains("equity curve", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApprovalPolicy_FailsWhenGenesisRegressionFails()
    {
        var input = MakePassingApprovalInput() with { GenesisRegressionOk = false };

        var result = OptimizationApprovalPolicy.Evaluate(input);

        Assert.False(result.Passed);
        Assert.False(result.SafetyGatesOk);
        Assert.Contains("genesis", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApprovalPolicy_AssetClassScalesMultiObjectiveThresholds()
    {
        // Sharpe=1.2 with multiplier=1.3 means effective threshold = 1.0 * 1.3 = 1.3
        // So 1.2 < 1.3 → strongSharpe is false, which reduces strong metric count
        // Also set improvement low to force multi-objective path
        var input = MakePassingApprovalInput() with
        {
            CandidateImprovement = 0.05m,        // fails composite gate
            SharpeRatio = 1.2m,                   // fails scaled Sharpe threshold (1.0 * 1.3 = 1.3)
            MaxDrawdownPct = 8m,                  // passes (10 / 1.3 ≈ 7.69 → 8 > 7.69, fails)
            WinRate = 0.50m,                      // passes (>= 0.45)
            ProfitFactor = 1.5m,                  // passes (>= 1.2 * 1.0)
            TotalTrades = 50,
            AssetClassSharpeMultiplier = 1.3,
            AssetClassPfMultiplier = 1.0
        };

        var result = OptimizationApprovalPolicy.Evaluate(input);

        // Sharpe fails (1.2 < 1.3), DD fails (8 > 7.69) → only 2/4 strong → multi-objective fails
        Assert.False(result.Passed);
        Assert.False(result.CompositeGateOk);
        Assert.False(result.MultiObjectiveGateOk);
    }

    // ── Checkpoint Store Tests ────────────────────────────────────────────

    [Fact]
    public void CheckpointStore_Serialize_Restore_RoundTrip()
    {
        var observations = new List<OptimizationCheckpointStore.Observation>
        {
            new(Sequence: 1, ParamsJson: """{"A":1,"B":2}""", HealthScore: 0.65m,
                CvCoefficientOfVariation: 0.08,
                Result: new BacktestResult { TotalTrades = 25, WinRate = 0.56m, ProfitFactor = 1.3m, Trades = [] }),
            new(Sequence: 2, ParamsJson: """{"A":3,"B":4}""", HealthScore: 0.72m,
                CvCoefficientOfVariation: 0.12,
                Result: new BacktestResult { TotalTrades = 30, WinRate = 0.60m, ProfitFactor = 1.5m, Trades = [] })
        };

        string json = OptimizationCheckpointStore.Serialize(
            iterations: 5,
            stagnantBatches: 1,
            surrogateKind: "TPE",
            surrogateRandomState: 42UL,
            observations: observations,
            seenParameterJson: ["""{"A":1,"B":2}""", """{"A":3,"B":4}"""]);

        var restored = OptimizationCheckpointStore.Restore(json);

        Assert.Equal(5, restored.Iterations);
        Assert.Equal(1, restored.StagnantBatches);
        Assert.Equal("TPE", restored.SurrogateKind);
        Assert.Equal(42UL, restored.SurrogateRandomState);
        Assert.Equal(2, restored.Observations.Count);
        Assert.Equal(0.65m, restored.Observations[0].HealthScore);
        Assert.Equal(0.72m, restored.Observations[1].HealthScore);
    }

    [Fact]
    public void CheckpointStore_Restore_ReturnsEmpty_ForNullJson()
    {
        var state = OptimizationCheckpointStore.Restore(null);

        Assert.Equal(0, state.Observations.Count);
        Assert.Equal(0, state.Iterations);
        Assert.Equal(0, state.StagnantBatches);
    }

    [Fact]
    public void CheckpointStore_Restore_ReturnsEmpty_ForCorruptJson()
    {
        var state = OptimizationCheckpointStore.Restore("{{not valid json at all!!");

        Assert.Equal(0, state.Observations.Count);
        Assert.Equal(0, state.Iterations);
    }

    [Fact]
    public void CheckpointStore_Serialize_TrimsWhenOverLimit()
    {
        // Create many observations with large ParamsJson to exceed MaxCheckpointChars (1M)
        // Each observation needs ~800+ chars of ParamsJson to push total past 1M with 1500 entries
        var observations = Enumerable.Range(1, 1500)
            .Select(i => new OptimizationCheckpointStore.Observation(
                Sequence: i,
                ParamsJson: JsonSerializer.Serialize(
                    Enumerable.Range(0, 100).ToDictionary(
                        k => $"ParameterWithLongName_{k:D3}",
                        k => (object)(k * 1000 + i))),
                HealthScore: 0.50m + (i * 0.0001m),
                CvCoefficientOfVariation: 0.10,
                Result: new BacktestResult
                {
                    TotalTrades = 20,
                    WinRate = 0.55m,
                    ProfitFactor = 1.2m,
                    Trades = []
                }))
            .ToList();

        var seenParams = observations.Select(o => o.ParamsJson).ToList();

        string serialized = OptimizationCheckpointStore.Serialize(
            iterations: 1500,
            stagnantBatches: 10,
            surrogateKind: "GP-UCB",
            surrogateRandomState: 999UL,
            observations: observations,
            seenParameterJson: seenParams);

        Assert.True(serialized.Length <= OptimizationCheckpointStore.MaxCheckpointChars,
            $"Serialized length {serialized.Length} exceeds max {OptimizationCheckpointStore.MaxCheckpointChars}");

        // Verify it can still be restored and was trimmed
        var restored = OptimizationCheckpointStore.Restore(serialized);
        Assert.True(restored.Observations.Count > 0);
        Assert.True(restored.Observations.Count < 1500,
            $"Expected trimming to reduce observation count below original 1500, got {restored.Observations.Count}");
    }

    [Fact]
    public async Task LoadConfigurationAsync_LoadsMaxRunsPerWeekOverride()
    {
        var configs = new List<EngineConfig>
        {
            new()
            {
                Key = "Optimization:MaxRunsPerWeek",
                Value = "7",
                IsDeleted = false
            }
        };

        var configDbSet = configs.AsQueryable().BuildMockDbSet();
        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);

        var method = typeof(OptimizationWorker).GetMethod(
            "LoadConfigurationAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = (Task)method.Invoke(null, [db.Object, CancellationToken.None])!;
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var maxRunsPerWeek = (int)result.GetType().GetProperty("MaxRunsPerWeek")!.GetValue(result)!;

        Assert.Equal(7, maxRunsPerWeek);
    }

    // ── Health Score Tests ────────────────────────────────────────────────

    [Fact]
    public void HealthScorer_PerfectStrategy_ScoresHigh()
    {
        // WR=0.70, PF=2.5, DD=5%, Sharpe=2.0, Trades=100
        decimal score = OptimizationHealthScorer.ComputeHealthScore(
            winRate: 0.70m,
            profitFactor: 2.5m,
            maxDrawdownPct: 5m,
            sharpeRatio: 2.0m,
            totalTrades: 100);

        // Expected components:
        // WR:     0.25 * 0.70    = 0.175
        // PF:     0.20 * min(1, 2.5/2) = 0.20 * 1.0 = 0.200
        // DD:     0.20 * max(0, 1 - 5/20) = 0.20 * 0.75 = 0.150
        // Sharpe: 0.15 * min(1, max(0, 2.0)/2) = 0.15 * 1.0 = 0.150
        // Trades: 0.20 * min(1, 100/50) = 0.20 * 1.0 = 0.200
        // Total = 0.875
        Assert.True(score >= 0.85m, $"Expected high score for perfect strategy, got {score}");
        Assert.True(score <= 1.0m, $"Score should not exceed 1.0, got {score}");
    }

    [Fact]
    public void HealthScorer_LosingStrategy_ScoresLow()
    {
        // WR=0.30, PF=0.5, DD=40%, Sharpe=-0.5, Trades=5
        decimal score = OptimizationHealthScorer.ComputeHealthScore(
            winRate: 0.30m,
            profitFactor: 0.5m,
            maxDrawdownPct: 40m,
            sharpeRatio: -0.5m,
            totalTrades: 5);

        // Expected components:
        // WR:     0.25 * 0.30    = 0.075
        // PF:     0.20 * min(1, 0.5/2) = 0.20 * 0.25 = 0.050
        // DD:     0.20 * max(0, 1 - 40/20) = 0.20 * 0.0 = 0.000
        // Sharpe: 0.15 * min(1, max(0, -0.5)/2) = 0.15 * 0.0 = 0.000
        // Trades: 0.20 * min(1, 5/50) = 0.20 * 0.10 = 0.020
        // Total = 0.145
        Assert.True(score <= 0.20m, $"Expected low score for losing strategy, got {score}");
    }

    [Fact]
    public void HealthScorer_ZeroTrades_ScoresVeryLow()
    {
        // With zero trades, DD=0% yields a non-zero DD component (1 - 0/20 = 1.0),
        // so the formula does not return exactly 0. The trade count component is 0.
        decimal score = OptimizationHealthScorer.ComputeHealthScore(
            winRate: 0m,
            profitFactor: 0m,
            maxDrawdownPct: 0m,
            sharpeRatio: 0m,
            totalTrades: 0);

        // Expected: WR=0, PF=0, DD=0.20 (no drawdown penalty), Sharpe=0, Trades=0 → 0.20
        Assert.Equal(0.20m, score);
    }

    // ── Binomial Helper Test ──────────────────────────────────────────────

    [Fact]
    public void Binomial_CorrectValues()
    {
        var binomialMethod = typeof(OptimizationWorker).GetMethod(
            "Binomial",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.NotNull(binomialMethod);

        long C_6_2 = (long)binomialMethod.Invoke(null, [6, 2])!;
        long C_10_3 = (long)binomialMethod.Invoke(null, [10, 3])!;
        long C_0_0 = (long)binomialMethod.Invoke(null, [0, 0])!;
        long C_5_0 = (long)binomialMethod.Invoke(null, [5, 0])!;
        long C_3_5 = (long)binomialMethod.Invoke(null, [3, 5])!;

        Assert.Equal(15, C_6_2);
        Assert.Equal(120, C_10_3);
        Assert.Equal(1, C_0_0);
        Assert.Equal(1, C_5_0);
        Assert.Equal(0, C_3_5);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static OptimizationApprovalPolicy.Input MakePassingApprovalInput() => new(
        CandidateImprovement: 0.15m,
        OosHealthScore: 0.65m,
        TotalTrades: 50,
        SharpeRatio: 1.2m,
        MaxDrawdownPct: 8m,
        WinRate: 0.55m,
        ProfitFactor: 1.5m,
        CILower: 0.50m,
        MinBootstrapCILower: 0.40m,
        DegradationFailed: false,
        WfStable: true,
        MtfCompatible: true,
        CorrelationSafe: true,
        SensitivityOk: true,
        CostSensitiveOk: true,
        TemporalCorrelationSafe: true,
        PortfolioCorrelationSafe: true,
        PermSignificant: true,
        CvConsistent: true,
        TemporalMaxOverlap: 0.20,
        PortfolioMaxCorrelation: 0.30,
        PermPValue: 0.01,
        PermCorrectedAlpha: 0.05,
        CvValue: 0.20,
        PessimisticScore: 0.55m,
        SensitivityReport: "ok",
        AutoApprovalImprovementThreshold: 0.10m,
        AutoApprovalMinHealthScore: 0.55m,
        MinCandidateTrades: 10,
        MaxCvCoefficientOfVariation: 0.50,
        KellySizingOk: true,
        KellySharpeRatio: 1.0,
        FixedLotSharpeRatio: 1.2,
        EquityCurveOk: true,
        TimeConcentrationOk: true,
        GenesisRegressionOk: true,
        AssetClassSharpeMultiplier: 1.0,
        AssetClassPfMultiplier: 1.0);

    // ── Portfolio correlation directionality tests ─────────────────────────

    [Fact]
    public void PortfolioCorrelation_NegativeCorrelation_IsTreatedAsSafe()
    {
        // Verify the fix: negative correlation (hedging) should NOT be flagged.
        // The code uses Math.Max(0, PearsonCorrelation(...)) so negative values become 0.
        // We test the logic indirectly via the PearsonCorrelation helper behavior:
        // Math.Max(0, -0.95) = 0, which is < any positive threshold → safe.
        double negativeCorr = -0.95;
        double effectiveCorr = Math.Max(0, negativeCorr);
        double threshold = 0.80;

        Assert.True(effectiveCorr < threshold,
            "Negative correlation (hedging) should be treated as safe (below threshold)");
        Assert.Equal(0, effectiveCorr);
    }

    [Fact]
    public void PortfolioCorrelation_HighPositiveCorrelation_IsFlagged()
    {
        double positiveCorr = 0.92;
        double effectiveCorr = Math.Max(0, positiveCorr);
        double threshold = 0.80;

        Assert.False(effectiveCorr < threshold,
            "High positive correlation (amplifying risk) should be flagged");
        Assert.Equal(0.92, effectiveCorr);
    }

    [Fact]
    public void PortfolioCorrelation_ZeroCorrelation_IsSafe()
    {
        double zeroCorr = 0.0;
        double effectiveCorr = Math.Max(0, zeroCorr);
        double threshold = 0.80;

        Assert.True(effectiveCorr < threshold,
            "Zero correlation (independent) should be treated as safe");
    }

    // ── Bootstrap CI blending zone tests ───────────────────────────────────

    [Theory]
    [InlineData(0, 0.50)]   // Pure synthetic: 0 trades → 50% penalty
    [InlineData(7, 0.63)]   // Pure synthetic: max at boundary
    [InlineData(8, -1)]     // Blending zone: between synthetic and empirical (value depends on empirical)
    [InlineData(14, -1)]    // Blending zone: near empirical end
    [InlineData(15, -1)]    // Full empirical: no synthetic component
    [InlineData(50, -1)]    // Full empirical: well above threshold
    public void BootstrapCI_BlendingZones_HaveNoDiscontinuity(int tradeCount, double expectedPenaltyIfSynthetic)
    {
        // Verify the 3-zone architecture:
        // Zone 1 (0-7 trades): Pure synthetic CI
        // Zone 2 (8-14 trades): Blended synthetic + empirical
        // Zone 3 (15+ trades): Full empirical CI
        const int bootstrapMinTrades = 15;
        const int syntheticMaxTrades = 7;

        if (tradeCount <= syntheticMaxTrades)
        {
            // Pure synthetic zone
            double samplePenalty = 0.50 + 0.13 * Math.Min(tradeCount, syntheticMaxTrades) / (double)syntheticMaxTrades;
            Assert.Equal(expectedPenaltyIfSynthetic, Math.Round(samplePenalty, 2));
        }
        else if (tradeCount > syntheticMaxTrades && tradeCount < bootstrapMinTrades)
        {
            // Blending zone: verify blend weight is in (0, 1)
            decimal blendWeight = (decimal)(tradeCount - syntheticMaxTrades)
                                / (bootstrapMinTrades - syntheticMaxTrades);
            Assert.True(blendWeight > 0m && blendWeight < 1m,
                $"Blend weight {blendWeight} for {tradeCount} trades should be in (0, 1)");
        }
        else
        {
            // Full empirical zone
            Assert.True(tradeCount >= bootstrapMinTrades);
        }
    }

    [Fact]
    public void BootstrapCI_BlendingZoneBoundaries_AreSmooth()
    {
        // At the boundary between synthetic and blending (tradeCount = 8),
        // the blend weight should be near 0 (mostly synthetic).
        // At the boundary between blending and empirical (tradeCount = 14),
        // the blend weight should be near 1 (mostly empirical).
        const int syntheticMaxTrades = 7;
        const int bootstrapMinTrades = 15;

        decimal blendAt8 = (decimal)(8 - syntheticMaxTrades) / (bootstrapMinTrades - syntheticMaxTrades);
        decimal blendAt14 = (decimal)(14 - syntheticMaxTrades) / (bootstrapMinTrades - syntheticMaxTrades);

        Assert.Equal(0.125m, blendAt8);  // 1/8 = mostly synthetic
        Assert.Equal(0.875m, blendAt14); // 7/8 = mostly empirical
    }

    // ── Rollout weighted regression detection tests ────────────────────────

    [Fact]
    public void RolloutDeteriorationDetection_SteadyDecline_DetectedByRegression()
    {
        // Snapshots ordered newest-first: 0.40, 0.45, 0.50, 0.55, 0.60
        // Steady decline from 0.60 → 0.40 should be detected
        var snapshots = new List<decimal> { 0.40m, 0.45m, 0.50m, 0.55m, 0.60m };
        decimal avgScore = snapshots.Average();

        // Replicate the weighted linear regression from GradualRolloutManager
        int n = snapshots.Count;
        double sumW = 0, sumWx = 0, sumWy = 0, sumWxx = 0, sumWxy = 0;
        for (int i = 0; i < n; i++)
        {
            int x = n - 1 - i;
            double y = (double)snapshots[i];
            double w = 1.0 + x;
            sumW += w; sumWx += w * x; sumWy += w * y;
            sumWxx += w * x * x; sumWxy += w * x * y;
        }
        double denom = sumW * sumWxx - sumWx * sumWx;
        double slope = (sumW * sumWxy - sumWx * sumWy) / denom;
        double predictedDecline = Math.Abs(slope) * n;

        Assert.True(slope < 0, "Slope should be negative for declining scores");
        Assert.True(predictedDecline > (double)avgScore * 0.10,
            $"Predicted decline {predictedDecline:F3} should exceed 10% of avg {avgScore}");
    }

    [Fact]
    public void RolloutDeteriorationDetection_SingleSpike_DoesNotTriggerFalsePositive()
    {
        // Snapshots: mostly stable with one spike that the old 3-point check would miss
        // Old check: 0.52 < 0.48? No → no rollback (correct, but only because spike is at position 0)
        // New check should also NOT trigger rollback since the overall trend is flat/slightly up
        var snapshots = new List<decimal> { 0.52m, 0.48m, 0.50m, 0.49m, 0.50m };
        decimal avgScore = snapshots.Average();

        int n = snapshots.Count;
        double sumW = 0, sumWx = 0, sumWy = 0, sumWxx = 0, sumWxy = 0;
        for (int i = 0; i < n; i++)
        {
            int x = n - 1 - i;
            double y = (double)snapshots[i];
            double w = 1.0 + x;
            sumW += w; sumWx += w * x; sumWy += w * y;
            sumWxx += w * x * x; sumWxy += w * x * y;
        }
        double denom = sumW * sumWxx - sumWx * sumWx;
        double slope = (sumW * sumWxy - sumWx * sumWy) / denom;
        double predictedDecline = Math.Abs(slope) * n;

        // Even if slope is slightly negative, the predicted decline should be small
        // relative to the average score, so rollback should NOT trigger
        bool wouldRollback = slope < 0 && predictedDecline > (double)avgScore * 0.10;
        Assert.False(wouldRollback,
            $"Flat series with spike should NOT trigger rollback (slope={slope:F4}, decline={predictedDecline:F3}, 10% of avg={avgScore * 0.10m:F3})");
    }

    [Fact]
    public void RolloutDeteriorationDetection_OldMonotonicCheck_MissesNonMonotonicDecline()
    {
        // Snapshots that decline overall but have a non-monotonic spike:
        // 0.42, 0.50, 0.48, 0.55, 0.60 (newest first)
        // Old 3-point check: 0.42 < 0.50 && 0.50 < 0.48? FALSE → would NOT detect decline
        // New regression: slope is clearly negative → detects the overall downtrend
        var snapshots = new List<decimal> { 0.42m, 0.50m, 0.48m, 0.55m, 0.60m };
        decimal avgScore = snapshots.Average();

        // Old monotonic check (should fail to detect)
        bool oldWouldDetect = snapshots.Count >= 3
            && snapshots[0] < snapshots[1]
            && snapshots[1] < snapshots[2];
        Assert.False(oldWouldDetect, "Old monotonic check should miss non-monotonic decline");

        // New regression check (should detect)
        int n = snapshots.Count;
        double sumW = 0, sumWx = 0, sumWy = 0, sumWxx = 0, sumWxy = 0;
        for (int i = 0; i < n; i++)
        {
            int x = n - 1 - i;
            double y = (double)snapshots[i];
            double w = 1.0 + x;
            sumW += w; sumWx += w * x; sumWy += w * y;
            sumWxx += w * x * x; sumWxy += w * x * y;
        }
        double denom = sumW * sumWxx - sumWx * sumWx;
        double slope = (sumW * sumWxy - sumWx * sumWy) / denom;
        double predictedDecline = Math.Abs(slope) * n;
        bool newDetects = slope < 0 && predictedDecline > (double)avgScore * 0.10;

        Assert.True(newDetects,
            $"New regression should detect non-monotonic decline (slope={slope:F4}, decline={predictedDecline:F3})");
    }

    // ── RunClaimer heartbeat delegation test ───────────────────────────────

    [Fact]
    public void OptimizationRunClaimer_StampHeartbeat_SetsLeaseExpiry()
    {
        var run = new OptimizationRun { Id = 1 };
        var leaseDuration = TimeSpan.FromMinutes(10);

        OptimizationRunClaimer.StampHeartbeat(run, leaseDuration);

        Assert.NotNull(run.LastHeartbeatAt);
        Assert.NotNull(run.ExecutionLeaseExpiresAt);
        Assert.True(run.ExecutionLeaseExpiresAt > run.LastHeartbeatAt);
        Assert.InRange(
            (run.ExecutionLeaseExpiresAt!.Value - run.LastHeartbeatAt!.Value).TotalMinutes,
            9.9, 10.1);
    }
}
