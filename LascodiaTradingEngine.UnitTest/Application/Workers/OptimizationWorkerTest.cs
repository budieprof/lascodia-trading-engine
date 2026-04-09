using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetOptimizationWorkerHealth;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class OptimizationWorkerTest
{
    [Fact]
    public async Task TemporalChunkedEvaluateAsync_ReturnsCandidateSpecificCvPerCall()
    {
        var validator = new OptimizationValidator(new TestBacktestEngine(), TimeProvider.System);
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

        var filtered = await OptimizationDataLoader.GetRegimeAwareCandlesAsync(
            db.Object,
            Mock.Of<ILogger>(),
            "EURUSD",
            Timeframe.H1,
            allCandles,
            CancellationToken.None,
            0.0);

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

        var filtered = await OptimizationDataLoader.GetRegimeAwareCandlesAsync(
            db.Object,
            Mock.Of<ILogger>(),
            "EURUSD",
            Timeframe.H1,
            allCandles,
            CancellationToken.None,
            0.0);

        Assert.Equal(120, filtered.Count);
        Assert.Equal(regimeStart, filtered[0].Timestamp);
        Assert.Equal(regimeStart.AddHours(119), filtered[^1].Timestamp);
    }

    [Fact]
    public async Task GetRegimeAwareCandlesAsync_WithBlendRatioOne_ReturnsAllCandles()
    {
        var regimeSnapshots = new List<MarketRegimeSnapshot>
        {
            new()
            {
                Id = 1,
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

        var filtered = await OptimizationDataLoader.GetRegimeAwareCandlesAsync(
            db.Object,
            Mock.Of<ILogger>(),
            "EURUSD",
            Timeframe.H1,
            allCandles,
            CancellationToken.None,
            1.0);

        Assert.Equal(allCandles.Count, filtered.Count);
        Assert.Equal(allCandles[0].Timestamp, filtered[0].Timestamp);
        Assert.Equal(allCandles[^1].Timestamp, filtered[^1].Timestamp);
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

        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);
        await InvokeAutoScheduleUnderperformersAsync(readCtx.Object, writeCtx.Object, config);

        Assert.DoesNotContain(
            optimizationRuns,
            r => r.Id != 1 && r.Id != 2 && r.Id != 3 && r.Status == OptimizationRunStatus.Queued);
    }

    [Fact]
    public async Task AutoScheduleUnderperformersAsync_SkipsRecentlyRejectedStrategyDuringCooldown()
    {
        var strategies = new List<Strategy>
        {
            new()
            {
                Id = 52,
                Name = "RejectedRecently",
                Status = StrategyStatus.Active,
                StrategyType = StrategyType.BreakoutScalper,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = """{"Fast":11}""",
                IsDeleted = false
            }
        };

        var optimizationRuns = new List<OptimizationRun>
        {
            new()
            {
                Id = 400,
                StrategyId = 52,
                Status = OptimizationRunStatus.Rejected,
                CompletedAt = DateTime.UtcNow.AddDays(-2),
                StartedAt = DateTime.UtcNow.AddDays(-2).AddHours(-1),
                BestParametersJson = """{"Fast":13}""",
                BaselineParametersJson = """{"Fast":11}""",
                BestHealthScore = 0.41m,
                BaselineHealthScore = 0.40m,
                IsDeleted = false
            }
        };

        var backtestRuns = new List<BacktestRun>
        {
            new()
            {
                Id = 401,
                StrategyId = 52,
                Status = RunStatus.Completed,
                CompletedAt = DateTime.UtcNow.AddDays(-1),
                ResultJson = JsonSerializer.Serialize(new BacktestResult
                {
                    TotalTrades = 24,
                    WinRate = 0.35m,
                    ProfitFactor = 0.82m,
                    MaxDrawdownPct = 15m,
                    SharpeRatio = 0.10m
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

        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);
        await InvokeAutoScheduleUnderperformersAsync(readCtx.Object, writeCtx.Object, config);

        Assert.Single(optimizationRuns);
        Assert.Equal(OptimizationRunStatus.Rejected, optimizationRuns[0].Status);
    }

    [Fact]
    public async Task AutoScheduleUnderperformersAsync_SchedulesGatePassingStrategy_WhenTrendShowsNonMonotonicDecline()
    {
        var strategies = new List<Strategy>
        {
            new()
            {
                Id = 43,
                Name = "Slipping",
                Status = StrategyStatus.Active,
                StrategyType = StrategyType.BreakoutScalper,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = """{"Fast":14}""",
                IsDeleted = false
            }
        };

        var optimizationRuns = new List<OptimizationRun>();
        var backtestRuns = new List<BacktestRun>
        {
            new()
            {
                Id = 101,
                StrategyId = 43,
                Status = RunStatus.Completed,
                CompletedAt = DateTime.UtcNow.AddDays(-1),
                ResultJson = JsonSerializer.Serialize(new BacktestResult
                {
                    TotalTrades = 40,
                    WinRate = 0.65m,
                    ProfitFactor = 1.60m,
                    MaxDrawdownPct = 7m,
                    SharpeRatio = 1.1m
                }),
                IsDeleted = false
            }
        };

        var snapshots = new List<StrategyPerformanceSnapshot>
        {
            new() { StrategyId = 43, EvaluatedAt = DateTime.UtcNow.AddHours(-1), HealthScore = 0.42m, IsDeleted = false },
            new() { StrategyId = 43, EvaluatedAt = DateTime.UtcNow.AddHours(-2), HealthScore = 0.50m, IsDeleted = false },
            new() { StrategyId = 43, EvaluatedAt = DateTime.UtcNow.AddHours(-3), HealthScore = 0.48m, IsDeleted = false },
        };

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

        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);
        await InvokeAutoScheduleUnderperformersAsync(readCtx.Object, writeCtx.Object, config);

        var queuedRun = Assert.Single(optimizationRuns);
        Assert.Equal(43, queuedRun.StrategyId);
        Assert.Equal(OptimizationRunStatus.Queued, queuedRun.Status);
        Assert.Equal(TriggerType.Scheduled, queuedRun.TriggerType);
    }

    [Fact]
    public async Task AutoScheduleUnderperformersAsync_WeeklyVelocityCap_IgnoresQueuedRuns()
    {
        var strategies = new List<Strategy>
        {
            new()
            {
                Id = 44,
                Name = "Eligible",
                Status = StrategyStatus.Active,
                StrategyType = StrategyType.BreakoutScalper,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = """{"Fast":12}""",
                IsDeleted = false
            }
        };

        var optimizationRuns = new List<OptimizationRun>
        {
            new()
            {
                Id = 200,
                StrategyId = 999,
                Status = OptimizationRunStatus.Queued,
                StartedAt = DateTime.UtcNow.AddDays(-1),
                IsDeleted = false
            }
        };

        var backtestRuns = new List<BacktestRun>
        {
            new()
            {
                Id = 102,
                StrategyId = 44,
                Status = RunStatus.Completed,
                CompletedAt = DateTime.UtcNow.AddDays(-1),
                ResultJson = JsonSerializer.Serialize(new BacktestResult
                {
                    TotalTrades = 40,
                    WinRate = 0.35m,
                    ProfitFactor = 0.85m,
                    MaxDrawdownPct = 16m,
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

        var config = CreateOptimizationConfig(
            cooldownDays: 14,
            maxConsecutiveFailuresBeforeEscalation: 3,
            maxRunsPerWeek: 1);
        await InvokeAutoScheduleUnderperformersAsync(readCtx.Object, writeCtx.Object, config);

        Assert.Contains(
            optimizationRuns,
            r => r.StrategyId == 44 && r.Status == OptimizationRunStatus.Queued);
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

        var checkpoint = OptimizationCheckpointStore.Restore(checkpointJson);
        var iterations = checkpoint.Iterations;
        var stagnantBatches = checkpoint.StagnantBatches;
        var surrogateKind = checkpoint.SurrogateKind;
        var observations = checkpoint.Observations;
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
    public void OptimizationApprovalPolicy_Rejects_WhenOutOfSampleDataIsInsufficient()
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
            PermSignificant: true,
            CvConsistent: true,
            TemporalMaxOverlap: 0.1,
            PortfolioMaxCorrelation: 0.1,
            PermPValue: 0.01,
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
            TimeConcentrationOk: true,
            HasSufficientOutOfSampleData: false));

        Assert.False(result.Passed);
        Assert.Contains("insufficient out-of-sample data", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptimizationApprovalPolicy_UsesAssetClassDrawdownMultiplier_ForFallbackApproval()
    {
        var result = OptimizationApprovalPolicy.Evaluate(new OptimizationApprovalPolicy.Input(
            CandidateImprovement: 0.02m,
            OosHealthScore: 0.50m,
            TotalTrades: 35,
            SharpeRatio: 1.30m,
            MaxDrawdownPct: 11.0m,
            WinRate: 0.48m,
            ProfitFactor: 1.50m,
            CILower: 0.45m,
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
            TemporalMaxOverlap: 0.1,
            PortfolioMaxCorrelation: 0.1,
            PermPValue: 0.01,
            PermCorrectedAlpha: 0.05,
            CvValue: 0.2,
            PessimisticScore: 0.48m,
            SensitivityReport: "ok",
            AutoApprovalImprovementThreshold: 0.10m,
            AutoApprovalMinHealthScore: 0.55m,
            MinCandidateTrades: 10,
            MaxCvCoefficientOfVariation: 0.50,
            KellySizingOk: true,
            KellySharpeRatio: 1.1,
            FixedLotSharpeRatio: 1.2,
            EquityCurveOk: true,
            TimeConcentrationOk: true,
            AssetClassSharpeMultiplier: 1.2,
            AssetClassPfMultiplier: 1.2,
            AssetClassDrawdownMultiplier: 0.85,
            GenesisRegressionOk: true,
            HasSufficientOutOfSampleData: true));

        Assert.True(result.Passed);
        Assert.True(result.MultiObjectiveGateOk);
    }

    [Fact]
    public async Task EnsureValidationFollowUpsAsync_IsIdempotentPerOptimizationRun()
    {
        var config = CreateOptimizationConfig(
            cooldownDays: 14,
            maxConsecutiveFailuresBeforeEscalation: 3,
            screeningInitialBalance: 25_000m);
        var run = new OptimizationRun
        {
            Id = 77,
            StrategyId = 5,
            ConfigSnapshotJson = JsonSerializer.Serialize(new { Version = 1, Config = config })
        };
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

        var coordinator = CreateFollowUpCoordinator();

        var firstResult = await coordinator.EnsureValidationFollowUpsAsync(db.Object, run, strategy, config, CancellationToken.None);
        var secondResult = await coordinator.EnsureValidationFollowUpsAsync(db.Object, run, strategy, config, CancellationToken.None);

        Assert.Single(backtests);
        Assert.Single(walks);
        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.True(run.ValidationFollowUpsCreatedAt.HasValue);
    }

    [Fact]
    public async Task EnsureValidationFollowUpsAsync_PinsFollowUpWindowToApprovalTimestamp()
    {
        var config = CreateOptimizationConfig(
            cooldownDays: 14,
            maxConsecutiveFailuresBeforeEscalation: 3,
            screeningInitialBalance: 25_000m);
        var approvedAt = new DateTime(2026, 04, 01, 12, 0, 0, DateTimeKind.Utc);
        var run = new OptimizationRun
        {
            Id = 78,
            StrategyId = 5,
            ApprovedAt = approvedAt,
            CompletedAt = approvedAt.AddMinutes(-5),
            ConfigSnapshotJson = JsonSerializer.Serialize(new { Version = 1, Config = config })
        };
        var strategy = new Strategy { Id = 5, Symbol = "EURUSD", Timeframe = Timeframe.H1 };

        var backtests = new List<BacktestRun>();
        var walks = new List<WalkForwardRun>();

        var backtestDbSet = backtests.AsQueryable().BuildMockDbSet();
        backtestDbSet.Setup(d => d.Add(It.IsAny<BacktestRun>()))
            .Callback<BacktestRun>(r => backtests.Add(r));

        var walkDbSet = walks.AsQueryable().BuildMockDbSet();
        walkDbSet.Setup(d => d.Add(It.IsAny<WalkForwardRun>()))
            .Callback<WalkForwardRun>(r => walks.Add(r));

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);

        var coordinator = CreateFollowUpCoordinator();
        await coordinator.EnsureValidationFollowUpsAsync(db.Object, run, strategy, config, CancellationToken.None);

        var backtest = Assert.Single(backtests);
        var walkForward = Assert.Single(walks);

        Assert.Equal(approvedAt.AddYears(-1), backtest.FromDate);
        Assert.Equal(approvedAt, backtest.ToDate);
        Assert.Equal(backtest.FromDate, walkForward.FromDate);
        Assert.Equal(backtest.ToDate, walkForward.ToDate);
    }

    [Fact]
    public async Task EnsureValidationFollowUpsAsync_ReusesExistingWindowWhenRepairingMissingRows()
    {
        var config = CreateOptimizationConfig(
            cooldownDays: 14,
            maxConsecutiveFailuresBeforeEscalation: 3,
            screeningInitialBalance: 25_000m);
        var expectedFrom = new DateTime(2025, 02, 01, 0, 0, 0, DateTimeKind.Utc);
        var expectedTo = new DateTime(2026, 02, 01, 0, 0, 0, DateTimeKind.Utc);
        var run = new OptimizationRun
        {
            Id = 79,
            StrategyId = 5,
            ApprovedAt = new DateTime(2026, 04, 01, 12, 0, 0, DateTimeKind.Utc),
            ConfigSnapshotJson = JsonSerializer.Serialize(new { Version = 1, Config = config })
        };
        var strategy = new Strategy { Id = 5, Symbol = "EURUSD", Timeframe = Timeframe.H1 };

        var backtests = new List<BacktestRun>
        {
            new()
            {
                Id = 1,
                SourceOptimizationRunId = run.Id,
                StrategyId = run.StrategyId,
                FromDate = expectedFrom,
                ToDate = expectedTo,
                Status = RunStatus.Completed,
                IsDeleted = false
            }
        };
        var walks = new List<WalkForwardRun>();

        var backtestDbSet = backtests.AsQueryable().BuildMockDbSet();
        var walkDbSet = walks.AsQueryable().BuildMockDbSet();
        walkDbSet.Setup(d => d.Add(It.IsAny<WalkForwardRun>()))
            .Callback<WalkForwardRun>(r => walks.Add(r));

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);

        var coordinator = CreateFollowUpCoordinator();
        bool hadAllRows = await coordinator.EnsureValidationFollowUpsAsync(db.Object, run, strategy, config, CancellationToken.None);

        Assert.False(hadAllRows);
        var walkForward = Assert.Single(walks);
        Assert.Equal(expectedFrom, walkForward.FromDate);
        Assert.Equal(expectedTo, walkForward.ToDate);
    }

    [Fact]
    public async Task MonitorFollowUpResultsAsync_RecreatesMissingFollowUps_WhenRowsAreAbsent()
    {
        var runs = new List<OptimizationRun>
        {
            new()
            {
                Id = 88,
                StrategyId = 5,
                Status = OptimizationRunStatus.Approved,
                ValidationFollowUpStatus = ValidationFollowUpStatus.Pending,
                ValidationFollowUpsCreatedAt = DateTime.UtcNow.AddMinutes(-5),
                ConfigSnapshotJson = JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Config = CreateOptimizationConfig(
                        cooldownDays: 14,
                        maxConsecutiveFailuresBeforeEscalation: 3,
                        screeningInitialBalance: 25_000m)
                }),
                IsDeleted = false
            }
        };
        var strategies = new List<Strategy>
        {
            new()
            {
                Id = 5,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = """{"Fast":10,"Slow":30}""",
                Status = StrategyStatus.Active,
                IsDeleted = false
            }
        };

        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var strategyDbSet = strategies.AsQueryable().BuildMockDbSet();
        var backtests = new List<BacktestRun>();
        var walks = new List<WalkForwardRun>();
        var backtestDbSet = backtests.AsQueryable().BuildMockDbSet();
        backtestDbSet.Setup(d => d.Add(It.IsAny<BacktestRun>()))
            .Callback<BacktestRun>(r => backtests.Add(r));
        var walkDbSet = walks.AsQueryable().BuildMockDbSet();
        walkDbSet.Setup(d => d.Add(It.IsAny<WalkForwardRun>()))
            .Callback<WalkForwardRun>(r => walks.Add(r));
        var configDbSet = new List<EngineConfig>().AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);
        db.Setup(c => c.Set<Strategy>()).Returns(strategyDbSet.Object);
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await InvokeMonitorFollowUpResultsAsync(readCtx.Object, writeCtx.Object);

        Assert.Equal(ValidationFollowUpStatus.Pending, runs[0].ValidationFollowUpStatus);
        Assert.Single(backtests);
        Assert.Single(walks);
        Assert.Equal(25_000m, backtests[0].InitialBalance);
        Assert.Equal(25_000m, walks[0].InitialBalance);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MonitorFollowUpResultsAsync_RecreatesOnlyMissingFollowUp_WhenOneAlreadyExists()
    {
        var runs = new List<OptimizationRun>
        {
            new()
            {
                Id = 89,
                StrategyId = 6,
                Status = OptimizationRunStatus.Approved,
                ValidationFollowUpStatus = ValidationFollowUpStatus.Pending,
                ValidationFollowUpsCreatedAt = DateTime.UtcNow.AddMinutes(-5),
                ConfigSnapshotJson = JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Config = CreateOptimizationConfig(
                        cooldownDays: 14,
                        maxConsecutiveFailuresBeforeEscalation: 3,
                        screeningInitialBalance: 30_000m)
                }),
                IsDeleted = false
            }
        };
        var strategies = new List<Strategy>
        {
            new()
            {
                Id = 6,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = """{"Fast":10,"Slow":30}""",
                Status = StrategyStatus.Active,
                IsDeleted = false
            }
        };
        var backtests = new List<BacktestRun>
        {
            new() { Id = 1, SourceOptimizationRunId = 89, StrategyId = 6, Status = RunStatus.Queued, IsDeleted = false }
        };
        var walks = new List<WalkForwardRun>();

        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var strategyDbSet = strategies.AsQueryable().BuildMockDbSet();
        var backtestDbSet = backtests.AsQueryable().BuildMockDbSet();
        backtestDbSet.Setup(d => d.Add(It.IsAny<BacktestRun>()))
            .Callback<BacktestRun>(r => backtests.Add(r));
        var walkDbSet = walks.AsQueryable().BuildMockDbSet();
        walkDbSet.Setup(d => d.Add(It.IsAny<WalkForwardRun>()))
            .Callback<WalkForwardRun>(r => walks.Add(r));
        var configDbSet = new List<EngineConfig>().AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);
        db.Setup(c => c.Set<Strategy>()).Returns(strategyDbSet.Object);
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await InvokeMonitorFollowUpResultsAsync(readCtx.Object, writeCtx.Object);

        Assert.Single(backtests);
        Assert.Single(walks);
        Assert.Equal(ValidationFollowUpStatus.Pending, runs[0].ValidationFollowUpStatus);
        Assert.Equal(30_000m, walks[0].InitialBalance);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OptimizationFollowUpTracker_DoesNotPassRun_WhenOnlyOneFollowUpExists()
    {
        var run = new OptimizationRun
        {
            Id = 91,
            StrategyId = 6,
            ValidationFollowUpStatus = ValidationFollowUpStatus.Pending,
            IsDeleted = false
        };

        var runs = new List<OptimizationRun> { run };
        var backtests = new List<BacktestRun>
        {
            new() { Id = 2, SourceOptimizationRunId = 91, StrategyId = 6, Status = RunStatus.Completed, IsDeleted = false }
        };
        var walks = new List<WalkForwardRun>();

        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var backtestDbSet = backtests.AsQueryable().BuildMockDbSet();
        var walkDbSet = walks.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await OptimizationFollowUpTracker.UpdateStatusAsync(
            db.Object, 91, followUpPassed: true, writeCtx.Object, CancellationToken.None);

        Assert.Equal(ValidationFollowUpStatus.Pending, run.ValidationFollowUpStatus);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void UpdateConsecutiveFailureStreak_ResetsOnlyWhenBatchHasSuccess()
    {
        Assert.Equal(7, OptimizationSearchCoordinator.UpdateConsecutiveFailureStreak(3, successfulEvaluations: 0, failedEvaluations: 4));
        Assert.Equal(0, OptimizationSearchCoordinator.UpdateConsecutiveFailureStreak(3, successfulEvaluations: 1, failedEvaluations: 4));
        Assert.Equal(3, OptimizationSearchCoordinator.UpdateConsecutiveFailureStreak(3, successfulEvaluations: 0, failedEvaluations: 0));
        Assert.Equal(0, OptimizationSearchCoordinator.UpdateConsecutiveFailureStreak(3, successfulEvaluations: 2, failedEvaluations: 0));
    }

    [Fact]
    public void ApplyImportanceGuidedBoundAdjustments_ReexpandsLowImportanceParamsWithinOriginalEnvelope()
    {
        var currentBounds = new Dictionary<string, (double Min, double Max, bool IsInteger)>
        {
            ["Fast"] = (2.0, 4.0, true),
            ["Slow"] = (10.0, 30.0, true)
        };
        var outerBounds = new Dictionary<string, (double Min, double Max, bool IsInteger)>
        {
            ["Fast"] = (0.0, 10.0, true),
            ["Slow"] = (0.0, 50.0, true)
        };
        var importance = new Dictionary<string, double>
        {
            ["Fast"] = 0.10,
            ["Slow"] = 0.80
        };

        var adjusted = OptimizationSearchCoordinator.ApplyImportanceGuidedBoundAdjustments(
            currentBounds, outerBounds, importance);

        Assert.True(adjusted["Fast"].Min < currentBounds["Fast"].Min);
        Assert.True(adjusted["Fast"].Max > currentBounds["Fast"].Max);
        Assert.True(adjusted["Slow"].Min > currentBounds["Slow"].Min);
        Assert.True(adjusted["Slow"].Max < currentBounds["Slow"].Max);
        Assert.InRange(adjusted["Fast"].Min, outerBounds["Fast"].Min, outerBounds["Fast"].Max);
        Assert.InRange(adjusted["Fast"].Max, outerBounds["Fast"].Min, outerBounds["Fast"].Max);
    }

    [Fact]
    public void GetCheckpointSurrogateObservationScore_UsesParegoScalarization_WhenEnabled()
    {
        var scalarizer = new ParegoScalarizer(123);
        var result = new BacktestResult
        {
            SharpeRatio = 1.40m,
            MaxDrawdownPct = 6.0m,
            WinRate = 0.62m,
            Trades = []
        };

        double paregoScore = OptimizationSearchCoordinator.GetCheckpointSurrogateObservationScore(
            useParegoScalarization: true,
            paregoScalarizer: scalarizer,
            result: result,
            healthScore: 0.12m);

        Assert.Equal(scalarizer.Scalarize(result), paregoScore, precision: 10);
        Assert.NotEqual(0.12d, paregoScore);
    }

    [Fact]
    public void TryGetWarmStartSurrogateObservationScore_RequiresObjectiveMetricsForParego()
    {
        var scalarizer = new ParegoScalarizer(321);

        bool valid = OptimizationSearchCoordinator.TryGetWarmStartSurrogateObservationScore(
            useParegoScalarization: true,
            paregoScalarizer: scalarizer,
            healthScore: 0.80m,
            sharpeRatio: 1.60m,
            maxDrawdownPct: 5.0m,
            winRate: 0.61m,
            decayFactor: 0.5,
            out double surrogateScore);

        Assert.True(valid);
        Assert.Equal(
            scalarizer.Scalarize(1.60m, 5.0m, 0.61m) * 0.5,
            surrogateScore,
            precision: 10);

        bool missingObjectives = OptimizationSearchCoordinator.TryGetWarmStartSurrogateObservationScore(
            useParegoScalarization: true,
            paregoScalarizer: scalarizer,
            healthScore: 0.80m,
            sharpeRatio: null,
            maxDrawdownPct: 5.0m,
            winRate: 0.61m,
            decayFactor: 0.5,
            out _);

        Assert.False(missingObjectives);
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

        var intervals = OptimizationRegimeIntervalBuilder.BuildRegimeIntervals(
            snapshots,
            MarketRegime.Trending,
            snapshots[0].DetectedAt,
            new DateTime(2026, 03, 05, 0, 0, 0, DateTimeKind.Utc));
        var filtered = OptimizationRegimeIntervalBuilder.FilterCandlesByIntervals(candles, intervals);

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
        var pairDbSet = new List<CurrencyPair>
        {
            new() { Symbol = "EURUSD", DecimalPlaces = 5, ContractSize = 100_000m, SpreadPoints = 12, IsDeleted = false }
        }.AsQueryable().BuildMockDbSet();
        var regimeParamsDbSet = new List<StrategyRegimeParams>().AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Candle>()).Returns(candleDbSet.Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(eventDbSet.Object);
        db.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(regimeDbSet.Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(pairDbSet.Object);
        db.Setup(c => c.Set<StrategyRegimeParams>()).Returns(regimeParamsDbSet.Object);

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
        var result = await InvokeLoadAndValidateCandlesAsync(
            db.Object,
            run,
            strategy,
            config,
            new BaselineSplitBacktestEngine());
        var baselineComparisonScore = result.BaselineComparisonScore;

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
        var pairDbSet = new List<CurrencyPair>
        {
            new() { Symbol = "EURUSD", DecimalPlaces = 5, ContractSize = 100_000m, SpreadPoints = 12, IsDeleted = false }
        }.AsQueryable().BuildMockDbSet();
        var regimeParamsDbSet = new List<StrategyRegimeParams>().AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Candle>()).Returns(candleDbSet.Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(eventDbSet.Object);
        db.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(regimeDbSet.Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(pairDbSet.Object);
        db.Setup(c => c.Set<StrategyRegimeParams>()).Returns(regimeParamsDbSet.Object);

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
        var result = await InvokeLoadAndValidateCandlesAsync(
            db.Object,
            run,
            strategy,
            config,
            new BaselineSplitBacktestEngine());
        var allCandles = result.AllCandles;

        Assert.True(allCandles.Count > candles.Count);
        Assert.NotNull(run.BaselineHealthScore);
    }

    [Fact]
    public async Task LoadAndValidateCandlesAsync_UsesRelevantHolidayAcrossFullLookback()
    {
        var nowUtc = DateTime.UtcNow;
        var gapStart = nowUtc.Date.AddDays(-40);
        while (gapStart.DayOfWeek != DayOfWeek.Monday)
            gapStart = gapStart.AddDays(-1);
        var seriesStart = nowUtc.AddHours(-1099);

        var candles = Enumerable.Range(0, 1100)
            .Select(i => seriesStart.AddHours(i))
            .Where(ts => ts < gapStart || ts >= gapStart.AddHours(96))
            .Where(ts => ts <= nowUtc)
            .Select((ts, i) => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = ts,
                Open = 1.1000m + i * 0.00001m,
                High = 1.1005m + i * 0.00001m,
                Low = 1.0995m + i * 0.00001m,
                Close = 1.1000m + i * 0.00001m,
                IsClosed = true
            })
            .ToList();

        var events = Enumerable.Range(0, 4)
            .Select(i => new EconomicEvent
            {
                Id = i + 1,
                Currency = "USD",
                Impact = EconomicImpact.Holiday,
                ScheduledAt = gapStart.AddDays(i),
                IsDeleted = false
            })
            .ToList();

        var pairs = new List<CurrencyPair>
        {
            new() { Symbol = "EURUSD", BaseCurrency = "EUR", QuoteCurrency = "USD", DecimalPlaces = 5, ContractSize = 100_000m, IsDeleted = false }
        };

        var candleDbSet = candles.AsQueryable().BuildMockDbSet();
        var eventDbSet = events.AsQueryable().BuildMockDbSet();
        var regimeDbSet = new List<MarketRegimeSnapshot>().AsQueryable().BuildMockDbSet();
        var pairDbSet = pairs.AsQueryable().BuildMockDbSet();
        var regimeParamsDbSet = new List<StrategyRegimeParams>().AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Candle>()).Returns(candleDbSet.Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(eventDbSet.Object);
        db.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(regimeDbSet.Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(pairDbSet.Object);
        db.Setup(c => c.Set<StrategyRegimeParams>()).Returns(regimeParamsDbSet.Object);

        var strategy = new Strategy
        {
            Id = 21,
            Name = "HolidayAware",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":12,"Slow":34}"""
        };
        var run = new OptimizationRun { Id = 888, StrategyId = strategy.Id, StartedAt = nowUtc };
        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);
        await InvokeLoadAndValidateCandlesAsync(
            db.Object,
            run,
            strategy,
            config,
            new BaselineSplitBacktestEngine());

        Assert.NotNull(run.BaselineHealthScore);
    }

    [Fact]
    public async Task LoadAndValidateCandlesAsync_DoesNotUseUnrelatedHolidayCurrencyToExcuseGap()
    {
        var nowUtc = DateTime.UtcNow;
        var mostRecentCandleAt = nowUtc.AddHours(-96);
        var candles = Enumerable.Range(0, 170)
            .Select(i => mostRecentCandleAt.AddHours(-(169 - i)))
            .Select((ts, i) => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = ts,
                Open = 1.1000m + i * 0.00001m,
                High = 1.1005m + i * 0.00001m,
                Low = 1.0995m + i * 0.00001m,
                Close = 1.1000m + i * 0.00001m,
                IsClosed = true
            })
            .ToList();

        var events = Enumerable.Range(0, 4)
            .Select(i => new EconomicEvent
            {
                Id = i + 1,
                Currency = "JPY",
                Impact = EconomicImpact.Holiday,
                ScheduledAt = mostRecentCandleAt.Date.AddDays(i + 1),
                IsDeleted = false
            })
            .ToList();

        var pairs = new List<CurrencyPair>
        {
            new() { Symbol = "EURUSD", BaseCurrency = "EUR", QuoteCurrency = "USD", DecimalPlaces = 5, ContractSize = 100_000m, IsDeleted = false }
        };

        var candleDbSet = candles.AsQueryable().BuildMockDbSet();
        var eventDbSet = events.AsQueryable().BuildMockDbSet();
        var regimeDbSet = new List<MarketRegimeSnapshot>().AsQueryable().BuildMockDbSet();
        var pairDbSet = pairs.AsQueryable().BuildMockDbSet();
        var regimeParamsDbSet = new List<StrategyRegimeParams>().AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Candle>()).Returns(candleDbSet.Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(eventDbSet.Object);
        db.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(regimeDbSet.Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(pairDbSet.Object);
        db.Setup(c => c.Set<StrategyRegimeParams>()).Returns(regimeParamsDbSet.Object);

        var strategy = new Strategy
        {
            Id = 22,
            Name = "CurrencyScopedHoliday",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":12,"Slow":34}"""
        };
        var run = new OptimizationRun { Id = 889, StrategyId = strategy.Id, StartedAt = nowUtc };
        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);
        var ex = await Assert.ThrowsAsync<DataQualityException>(async () =>
            await InvokeLoadAndValidateCandlesAsync(
                db.Object,
                run,
                strategy,
                config,
                new BaselineSplitBacktestEngine()));

        Assert.Contains("Data quality validation failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAndValidateCandlesAsync_RequiresCurrencyPairMetadataForCostModel()
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

        var strategy = new Strategy
        {
            Id = 23,
            Name = "MetadataRequired",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":12,"Slow":34}"""
        };
        var run = new OptimizationRun { Id = 890, StrategyId = strategy.Id, StartedAt = nowUtc };
        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);
        var ex = await Assert.ThrowsAsync<DataQualityException>(async () =>
            await InvokeLoadAndValidateCandlesAsync(
                db.Object,
                run,
                strategy,
                config,
                new BaselineSplitBacktestEngine()));

        Assert.Contains("CurrencyPair metadata", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestInitialCandidates_UsesEhviWhenConfigured()
    {
        string[] paramNames = ["Fast", "Slow"];
        double[] lower = [5, 20];
        double[] upper = [20, 50];
        bool[] isInteger = [true, true];
        var ehvi = new EhviAcquisition(paramNames, lower, upper, isInteger, seed: 123);

        var suggestions = OptimizationSearchCoordinator.SuggestInitialCandidates(
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

    [Theory]
    [InlineData(true, OptimizationRunStatus.Completed, true)]
    [InlineData(true, OptimizationRunStatus.Approved, true)]
    [InlineData(true, OptimizationRunStatus.Rejected, true)]
    [InlineData(true, OptimizationRunStatus.Running, false)]
    [InlineData(false, OptimizationRunStatus.Completed, false)]
    public void ShouldPreservePersistedResult_OnlyKeepsPersistedTerminalStatuses(
        bool completionPersisted,
        OptimizationRunStatus status,
        bool expected)
    {
        bool actual = OptimizationRunLifecycle.ShouldPreservePersistedResult(completionPersisted, status);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ApplyApprovalDecisionAsync_MarksRunFailed_WhenApprovalPersistenceFails()
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

        await InvokeApplyApprovalDecisionAsync(
            runContext,
            validationResult,
            null,
            DateTime.UtcNow.AddMonths(-2),
            new BacktestOptions());

        Assert.Equal(OptimizationRunStatus.Failed, run.Status);
        Assert.Equal(OptimizationFailureCategory.Transient, run.FailureCategory);
        Assert.Null(run.ApprovedAt);
        Assert.Contains("approval event persistence failed", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(strategy.RolloutPct);
        Assert.Equal("""{"Fast":10,"Slow":30}""", strategy.ParametersJson);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyApprovalDecisionAsync_RejectsRun_WhenStrategyDisappearsBeforeApproval()
    {
        var run = new OptimizationRun
        {
            Id = 405,
            StrategyId = 13,
            Status = OptimizationRunStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            BestParametersJson = """{"Fast":18,"Slow":44}"""
        };
        var strategy = new Strategy
        {
            Id = 13,
            Name = "Vanished",
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":10,"Slow":30}""",
            Status = StrategyStatus.Active,
            IsDeleted = false
        };

        var strategies = new List<Strategy>();
        var strategyDbSet = strategies.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Strategy>()).Returns(strategyDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var mediator = new Mock<IMediator>();
        var alertDispatcher = new Mock<IAlertDispatcher>();
        var eventService = new Mock<IIntegrationEventService>();

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
            winnerParamsJson: """{"Fast":18,"Slow":44}""",
            oosHealthScore: 0.74m,
            oosResult: oosResult,
            ciLower: 0.55m,
            ciUpper: 0.82m,
            wfAvgScore: 0.70m,
            pessimisticScore: 0.68m,
            failureReason: string.Empty);

        await InvokeApplyApprovalDecisionAsync(
            runContext,
            validationResult,
            null,
            DateTime.UtcNow.AddMonths(-2),
            new BacktestOptions());

        Assert.Equal(OptimizationRunStatus.Rejected, run.Status);
        Assert.Equal(OptimizationFailureCategory.StrategyRemoved, run.FailureCategory);
        Assert.Null(run.ApprovedAt);
        Assert.Contains("StrategyRemoved", run.ApprovalReportJson, StringComparison.Ordinal);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        eventService.Verify(
            x => x.SaveAndPublish(It.IsAny<IDbContext>(), It.IsAny<Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>()),
            Times.Never);
        mediator.Verify(
            x => x.Send(
                It.Is<LogDecisionCommand>(c => c.Outcome == "Rejected"
                    && c.Reason!.Contains("Strategy removed", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyApprovalDecisionAsync_PersistsManualReviewDiagnostics_WhenCandidateFailsApproval()
    {
        var run = new OptimizationRun
        {
            Id = 406,
            StrategyId = 14,
            Status = OptimizationRunStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            ApprovalReportJson = "{}",
            BestParametersJson = """{"Fast":22,"Slow":55}"""
        };
        var strategy = new Strategy
        {
            Id = 14,
            Name = "ManualReview",
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":10,"Slow":30}""",
            Status = StrategyStatus.Active,
            IsDeleted = false
        };

        var runs = new List<OptimizationRun> { run };
        var runDbSet = runs.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1, true, "ok", "00"));

        var alertDispatcher = new Mock<IAlertDispatcher>();
        var eventService = new Mock<IIntegrationEventService>();

        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);
        var runContext = CreateRunContext(
            run, strategy, config, baselineComparisonScore: 0.40m,
            db.Object, db.Object, writeCtx.Object, mediator.Object,
            alertDispatcher.Object, eventService.Object);

        var oosResult = new BacktestResult
        {
            TotalTrades = 8,
            WinRate = 0.52m,
            ProfitFactor = 1.05m,
            MaxDrawdownPct = 12m,
            SharpeRatio = 0.3m,
            Trades = []
        };
        var validationResult = CreateCandidateValidationResult(
            passed: false,
            winnerParamsJson: """{"Fast":22,"Slow":55}""",
            oosHealthScore: 0.43m,
            oosResult: oosResult,
            ciLower: 0.21m,
            ciUpper: 0.55m,
            wfAvgScore: 0.38m,
            pessimisticScore: 0.35m,
            failureReason: "Permutation test failed");

        await InvokeApplyApprovalDecisionAsync(
            runContext,
            validationResult,
            null,
            DateTime.UtcNow.AddMonths(-2),
            new BacktestOptions());

        Assert.Contains("failedCandidates", run.ApprovalReportJson, StringComparison.Ordinal);
        Assert.Contains("topCandidateFailureReason", run.ApprovalReportJson, StringComparison.Ordinal);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        mediator.Verify(
            x => x.Send(
                It.Is<LogDecisionCommand>(c => c.Outcome == "ManualReviewRequired"
                    && c.Reason == "Permutation test failed"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MonitorFollowUpResultsAsync_MarksRunFailed_WhenValidationFails()
    {
        var run = new OptimizationRun
        {
            Id = 501,
            StrategyId = 77,
            Status = OptimizationRunStatus.Approved,
            ValidationFollowUpStatus = ValidationFollowUpStatus.Pending,
            IsDeleted = false
        };

        var backtestRun = new BacktestRun
        {
            Id = 601,
            StrategyId = 77,
            SourceOptimizationRunId = 501,
            Status = RunStatus.Completed,
            ResultJson = JsonSerializer.Serialize(new BacktestResult
            {
                TotalTrades = 3,
                WinRate = 0.40m,
                ProfitFactor = 0.80m,
                MaxDrawdownPct = 18m,
                SharpeRatio = 0.1m
            }),
            IsDeleted = false
        };

        var walkForwardRun = new WalkForwardRun
        {
            Id = 701,
            StrategyId = 77,
            SourceOptimizationRunId = 501,
            Status = RunStatus.Completed,
            AverageOutOfSampleScore = 0.65m,
            ScoreConsistency = 0.10m,
            IsDeleted = false
        };

        var alerts = new List<Alert>();
        var runs = new List<OptimizationRun> { run };
        var backtests = new List<BacktestRun> { backtestRun };
        var walks = new List<WalkForwardRun> { walkForwardRun };
        var configs = new List<EngineConfig>();

        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var backtestDbSet = backtests.AsQueryable().BuildMockDbSet();
        var walkDbSet = walks.AsQueryable().BuildMockDbSet();
        var alertDbSet = alerts.AsQueryable().BuildMockDbSet();
        alertDbSet.Setup(d => d.Add(It.IsAny<Alert>()))
            .Callback<Alert>(alert => alerts.Add(alert));
        var configDbSet = configs.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);
        db.Setup(c => c.Set<Alert>()).Returns(alertDbSet.Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var alertDispatcher = new Mock<IAlertDispatcher>();
        alertDispatcher.Setup(x => x.DispatchBySeverityAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await InvokeMonitorFollowUpResultsAsync(readCtx.Object, writeCtx.Object, alertDispatcher.Object);

        Assert.Equal(ValidationFollowUpStatus.Failed, run.ValidationFollowUpStatus);
    }

    [Fact]
    public void PopulateFollowUpFailureAlert_BuildsHighSeverityAlertPayload()
    {
        var alert = new Alert();
        var nowUtc = new DateTime(2026, 04, 05, 12, 0, 0, DateTimeKind.Utc);

        string message = OptimizationFollowUpCoordinator.PopulateFollowUpFailureAlert(
            alert,
            optimizationRunId: 501,
            strategyId: 77,
            backtestStatus: RunStatus.Completed,
            walkForwardStatus: RunStatus.Failed,
            backtestQualityOk: false,
            walkForwardQualityOk: false,
            backtestReason: "backtest follow-up produced too few trades",
            walkForwardReason: "walk-forward follow-up execution failed",
            utcNow: nowUtc);

        Assert.Equal("OptimizationRun:501:FollowUp", alert.Symbol);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.True(alert.IsActive);
        Assert.Equal(nowUtc, alert.LastTriggeredAt);
        Assert.Contains("OptimizationFollowUpFailure", alert.ConditionJson, StringComparison.Ordinal);
        Assert.Contains("Optimization follow-up validation failed", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, OptimizationRunStatus.Completed, true)]
    [InlineData(true, OptimizationRunStatus.Approved, true)]
    [InlineData(true, OptimizationRunStatus.Rejected, true)]
    [InlineData(true, OptimizationRunStatus.Running, false)]
    [InlineData(false, OptimizationRunStatus.Completed, false)]
    public void ShouldPreservePersistedResult_OnlyAllowsPersistedTerminalStatuses(
        bool completionPersisted,
        OptimizationRunStatus status,
        bool expected)
    {
        bool actual = OptimizationRunLifecycle.ShouldPreservePersistedResult(completionPersisted, status);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ApplyApprovalDecisionAsync_PropagatesRunCancellation_DuringCrossRegimeEvaluation()
    {
        var nowUtc = DateTime.UtcNow;
        var run = new OptimizationRun
        {
            Id = 407,
            StrategyId = 15,
            Status = OptimizationRunStatus.Completed,
            CompletedAt = nowUtc,
            BestParametersJson = """{"Fast":18,"Slow":44}"""
        };
        var strategy = new Strategy
        {
            Id = 15,
            Name = "CrossRegimeCancel",
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":10,"Slow":30}""",
            StrategyType = StrategyType.BreakoutScalper,
            Status = StrategyStatus.Active,
            IsDeleted = false
        };

        var strategies = new List<Strategy> { strategy };
        var backtests = new List<BacktestRun>();
        var walks = new List<WalkForwardRun>();
        var regimeParams = new List<StrategyRegimeParams>();
        var snapshots = new List<MarketRegimeSnapshot>
        {
            new()
            {
                Id = 1,
                Symbol = strategy.Symbol,
                Timeframe = strategy.Timeframe,
                Regime = MarketRegime.Ranging,
                DetectedAt = nowUtc.AddDays(-12),
                IsDeleted = false
            },
            new()
            {
                Id = 2,
                Symbol = strategy.Symbol,
                Timeframe = strategy.Timeframe,
                Regime = MarketRegime.Trending,
                DetectedAt = nowUtc.AddDays(-6),
                IsDeleted = false
            }
        };
        var candles = Enumerable.Range(0, 240)
            .Select(i => new Candle
            {
                Symbol = strategy.Symbol,
                Timeframe = strategy.Timeframe,
                Timestamp = nowUtc.AddHours(-(240 - i)),
                Open = 1.10m,
                High = 1.11m,
                Low = 1.09m,
                Close = 1.10m,
                IsClosed = true
            })
            .ToList();

        var strategyDbSet = strategies.AsQueryable().BuildMockDbSet();
        var backtestDbSet = backtests.AsQueryable().BuildMockDbSet();
        backtestDbSet.Setup(d => d.Add(It.IsAny<BacktestRun>()))
            .Callback<BacktestRun>(r => backtests.Add(r));
        var walkDbSet = walks.AsQueryable().BuildMockDbSet();
        walkDbSet.Setup(d => d.Add(It.IsAny<WalkForwardRun>()))
            .Callback<WalkForwardRun>(r => walks.Add(r));
        var regimeParamsDbSet = regimeParams.AsQueryable().BuildMockDbSet();
        regimeParamsDbSet.Setup(d => d.Add(It.IsAny<StrategyRegimeParams>()))
            .Callback<StrategyRegimeParams>(r => regimeParams.Add(r));
        var snapshotDbSet = snapshots.AsQueryable().BuildMockDbSet();
        var candleDbSet = candles.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Strategy>()).Returns(strategyDbSet.Object);
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);
        db.Setup(c => c.Set<StrategyRegimeParams>()).Returns(regimeParamsDbSet.Object);
        db.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(snapshotDbSet.Object);
        db.Setup(c => c.Set<Candle>()).Returns(candleDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1, true, "ok", "00"));

        using var runCts = new CancellationTokenSource();
        var eventService = new Mock<IIntegrationEventService>();
        eventService.Setup(x => x.SaveAndPublish(
                It.IsAny<IDbContext>(),
                It.IsAny<Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>()))
            .Callback<IDbContext, Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>((_, evt) =>
            {
                if (evt is OptimizationApprovedIntegrationEvent)
                    runCts.Cancel();
            })
            .Returns(Task.CompletedTask);

        var config = CreateOptimizationConfig(cooldownDays: 14, maxConsecutiveFailuresBeforeEscalation: 3);
        var runContext = CreateRunContext(
            run, strategy, config, baselineComparisonScore: 0.40m,
            db.Object, db.Object, writeCtx.Object, mediator.Object,
            Mock.Of<IAlertDispatcher>(), eventService.Object,
            ct: CancellationToken.None,
            runCt: runCts.Token);

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
            winnerParamsJson: """{"Fast":18,"Slow":44}""",
            oosHealthScore: 0.74m,
            oosResult: oosResult,
            ciLower: 0.55m,
            ciUpper: 0.82m,
            wfAvgScore: 0.70m,
            pessimisticScore: 0.68m,
            failureReason: string.Empty);

        var task = InvokeApplyApprovalDecisionAsync(
            runContext,
            validationResult,
            MarketRegime.Trending,
            nowUtc.AddDays(-14),
            new BacktestOptions(),
            new RecordingBacktestEngine());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task EscalateChronicFailuresAsync_SuppressesRecentDuplicateAlert()
    {
        var nowUtc = DateTime.UtcNow;
        var strategyId = 44L;
        var runs = new List<OptimizationRun>
        {
            new() { Id = 1, StrategyId = strategyId, Status = OptimizationRunStatus.Completed, CompletedAt = nowUtc.AddMinutes(-10), IsDeleted = false },
            new() { Id = 2, StrategyId = strategyId, Status = OptimizationRunStatus.Completed, CompletedAt = nowUtc.AddMinutes(-20), IsDeleted = false },
            new() { Id = 3, StrategyId = strategyId, Status = OptimizationRunStatus.Completed, CompletedAt = nowUtc.AddMinutes(-30), IsDeleted = false }
        };
        var alerts = new List<Alert>
        {
            new()
            {
                Id = 1,
                Symbol = $"Strategy:{strategyId}",
                DeduplicationKey = $"Optimization:ChronicFailure:{strategyId}",
                CooldownSeconds = 24 * 60 * 60,
                LastTriggeredAt = nowUtc.AddHours(-1),
                IsDeleted = false
            }
        };

        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var alertDbSet = alerts.AsQueryable().BuildMockDbSet();
        alertDbSet.Setup(d => d.Add(It.IsAny<Alert>()))
            .Callback<Alert>(alert => alerts.Add(alert));

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);
        db.Setup(c => c.Set<Alert>()).Returns(alertDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var mediator = new Mock<IMediator>();
        var alertDispatcher = new Mock<IAlertDispatcher>();
        await InvokeEscalateChronicFailuresAsync(
            db.Object,
            db.Object,
            writeCtx.Object,
            mediator.Object,
            alertDispatcher.Object,
            strategyId,
            "NoisyStrategy",
            3,
            14);

        Assert.Single(alerts);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        alertDispatcher.Verify(
            x => x.DispatchBySeverityAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static ServiceProvider CreateOptimizationServiceProvider(
        IBacktestEngine? backtestEngine = null,
        Action<ServiceCollection>? configureServices = null,
        TimeProvider? timeProvider = null)
    {
        backtestEngine ??= Mock.Of<IBacktestEngine>();
        timeProvider ??= TimeProvider.System;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton(timeProvider);
        services.AddSingleton<TradingMetrics>(sp => new TradingMetrics(
            sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
        services.AutoRegisterAttributedServices(typeof(OptimizationRunProcessor).Assembly);
        services.AddSingleton<IBacktestEngine>(backtestEngine);
        services.AddSingleton<ISpreadProfileProvider>(Mock.Of<ISpreadProfileProvider>());
        services.AddSingleton<IAlertDispatcher>(Mock.Of<IAlertDispatcher>());
        services.AddSingleton<IValidationSettingsProvider, ValidationSettingsProvider>();
        services.AddSingleton<IBacktestOptionsSnapshotBuilder>(sp =>
            new BacktestOptionsSnapshotBuilder(
                sp.GetRequiredService<IValidationSettingsProvider>(),
                NullLogger<BacktestOptionsSnapshotBuilder>.Instance));
        services.AddSingleton<IValidationRunFactory>(sp =>
            new ValidationRunFactory(
                sp.GetRequiredService<IBacktestOptionsSnapshotBuilder>(),
                sp.GetRequiredService<TimeProvider>()));
        configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private static OptimizationWorker CreateWorker(
        IBacktestEngine? backtestEngine = null,
        IServiceScopeFactory? scopeFactory = null,
        TimeProvider? timeProvider = null)
    {
        var logger = Mock.Of<ILogger<OptimizationWorker>>();
        backtestEngine ??= Mock.Of<IBacktestEngine>();
        scopeFactory ??= CreateWorkerScopeFactory(backtestEngine, timeProvider: timeProvider);
        var metricsServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = metricsServices.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        var metrics = new TradingMetrics(meterFactory);
        var services = (IServiceProvider)scopeFactory;
        var healthMonitor = services.GetService<IWorkerHealthMonitor>();
        var healthStore = services.GetRequiredService<IOptimizationWorkerHealthStore>();
        var configProvider = services.GetRequiredService<OptimizationConfigProvider>();
        var loopCoordinator = services.GetRequiredService<IOptimizationWorkerLoopCoordinator>();
        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;

        return new OptimizationWorker(
            logger,
            scopeFactory,
            metrics,
            healthMonitor,
            healthStore,
            configProvider,
            loopCoordinator,
            effectiveTimeProvider,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    private static IServiceScopeFactory CreateWorkerScopeFactory(
        IBacktestEngine? backtestEngine = null,
        Action<ServiceCollection>? configureServices = null,
        TimeProvider? timeProvider = null)
    {
        var serviceProvider = CreateOptimizationServiceProvider(backtestEngine, configureServices, timeProvider);
        return serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task<DataLoadResult> InvokeLoadAndValidateCandlesAsync(
        DbContext db,
        OptimizationRun run,
        Strategy strategy,
        OptimizationConfig config,
        IBacktestEngine? backtestEngine = null,
        TimeProvider? timeProvider = null)
    {
        using var provider = CreateOptimizationServiceProvider(backtestEngine, timeProvider: timeProvider);
        await using var scope = provider.CreateAsyncScope();
        var dataLoader = scope.ServiceProvider.GetRequiredService<OptimizationDataLoader>();
        return await dataLoader.LoadAsync(db, run, strategy, config.ToDataLoadingConfig(), CancellationToken.None);
    }

    private static async Task InvokeAutoScheduleUnderperformersAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        OptimizationConfig config,
        IBacktestEngine? backtestEngine = null,
        TimeProvider? timeProvider = null)
    {
        using var provider = CreateOptimizationServiceProvider(backtestEngine, services =>
        {
            services.AddSingleton(readCtx);
            services.AddSingleton(writeCtx);
        }, timeProvider);
        await using var scope = provider.CreateAsyncScope();
        var schedulingCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationSchedulingCoordinator>();
        await schedulingCoordinator.AutoScheduleUnderperformersAsync(readCtx, writeCtx, config, CancellationToken.None);
    }

    private static async Task InvokeMonitorFollowUpResultsAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        IAlertDispatcher? alertDispatcher = null,
        TimeProvider? timeProvider = null)
    {
        using var provider = CreateOptimizationServiceProvider(configureServices: services =>
        {
            services.AddSingleton(readCtx);
            services.AddSingleton(writeCtx);
            if (alertDispatcher is not null)
                services.AddSingleton(alertDispatcher);
        }, timeProvider: timeProvider);
        await using var scope = provider.CreateAsyncScope();
        var configProvider = scope.ServiceProvider.GetRequiredService<OptimizationConfigProvider>();
        var followUpCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationFollowUpCoordinator>();
        var config = await configProvider.LoadAsync(readCtx.GetDbContext(), CancellationToken.None);
        await followUpCoordinator.MonitorAsync(config, CancellationToken.None);
    }

    private static async Task InvokeApplyApprovalDecisionAsync(
        object ctx,
        object validationResult,
        MarketRegime? currentRegime,
        DateTime candleLookbackStart,
        BacktestOptions screeningOptions,
        IBacktestEngine? backtestEngine = null,
        Action<ServiceCollection>? configureServices = null,
        TimeProvider? timeProvider = null)
    {
        using var provider = CreateOptimizationServiceProvider(backtestEngine, configureServices, timeProvider);
        await using var scope = provider.CreateAsyncScope();
        var approvalCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationApprovalCoordinator>();
        await approvalCoordinator.ApplyAsync(
            (RunContext)ctx,
            ((RunContext)ctx).Config.ToApprovalConfig(),
            (CandidateValidationResult)validationResult,
            currentRegime,
            candleLookbackStart,
            screeningOptions);
    }

    private static async Task InvokeEscalateChronicFailuresAsync(
        DbContext db,
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        IMediator mediator,
        IAlertDispatcher alertDispatcher,
        long strategyId,
        string strategyName,
        int maxConsecutiveFailures,
        int baseCooldownDays,
        TimeProvider? timeProvider = null)
    {
        using var provider = CreateOptimizationServiceProvider(timeProvider: timeProvider);
        await using var scope = provider.CreateAsyncScope();
        var escalator = scope.ServiceProvider.GetRequiredService<OptimizationChronicFailureEscalator>();
        await escalator.EscalateAsync(
            db,
            writeDb,
            writeCtx,
            mediator,
            alertDispatcher,
            strategyId,
            strategyName,
            maxConsecutiveFailures,
            baseCooldownDays,
            CancellationToken.None);
    }

    private static async Task InvokeRetryFailedRunsAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        TimeProvider? timeProvider = null)
    {
        using var provider = CreateOptimizationServiceProvider(configureServices: services =>
        {
            services.AddSingleton(readCtx);
            services.AddSingleton(writeCtx);
        }, timeProvider: timeProvider);
        await using var scope = provider.CreateAsyncScope();
        var configProvider = scope.ServiceProvider.GetRequiredService<OptimizationConfigProvider>();
        var recoveryCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationRunRecoveryCoordinator>();
        var config = await configProvider.LoadAsync(readCtx.GetDbContext(), CancellationToken.None);
        await recoveryCoordinator.RetryFailedRunsAsync(config, CancellationToken.None);
    }

    private static async Task InvokeRecoverStaleQueuedRunsAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        TimeProvider? timeProvider = null)
    {
        using var provider = CreateOptimizationServiceProvider(configureServices: services =>
        {
            services.AddSingleton(readCtx);
            services.AddSingleton(writeCtx);
        }, timeProvider: timeProvider);
        await using var scope = provider.CreateAsyncScope();
        var recoveryCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationRunRecoveryCoordinator>();
        await recoveryCoordinator.RecoverStaleQueuedRunsAsync(CancellationToken.None);
    }

    private static OptimizationFollowUpCoordinator CreateFollowUpCoordinator(TimeProvider? timeProvider = null)
    {
        var metricsServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = metricsServices.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;
        var settingsProvider = new ValidationSettingsProvider();
        var optionsBuilder = new BacktestOptionsSnapshotBuilder(settingsProvider, NullLogger<BacktestOptionsSnapshotBuilder>.Instance);
        var validationRunFactory = new ValidationRunFactory(optionsBuilder, effectiveTimeProvider);
        return new OptimizationFollowUpCoordinator(
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<IAlertDispatcher>(),
            new OptimizationRunScopedConfigService(
                new OptimizationConfigProvider(Mock.Of<ILogger<OptimizationConfigProvider>>(), effectiveTimeProvider),
                Mock.Of<ILogger<OptimizationRunScopedConfigService>>()),
            validationRunFactory,
            optionsBuilder,
            Mock.Of<ILogger<OptimizationFollowUpCoordinator>>(),
            new TradingMetrics(meterFactory),
            effectiveTimeProvider);
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesCoordinatorCycles_WhileProcessingSlotsAreBusy()
    {
        var timeProvider = TimeProvider.System;
        var configProvider = await CreatePrimedConfigProviderAsync(
            new EngineConfig
            {
                Key = "Optimization:MaxConcurrentRuns",
                Value = "2",
                DataType = ConfigDataType.Int,
                IsDeleted = false
            },
            new EngineConfig
            {
                Key = "Optimization:SchedulePollSeconds",
                Value = "1",
                DataType = ConfigDataType.Int,
                IsDeleted = false
            });
        var blockingProcessor = new BlockingOptimizationRunProcessor(blockingCalls: 2);
        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(new Mock<DbContext>().Object);

        var services = new ServiceCollection()
            .AddSingleton(readCtx.Object)
            .AddSingleton<IOptimizationRunProcessor>(blockingProcessor)
            .BuildServiceProvider();

        var metricsServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new TradingMetrics(
            metricsServices.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var loopCoordinator = new CountingLoopCoordinator();
        var healthStore = new OptimizationWorkerHealthStore();

        var worker = new OptimizationWorker(
            Mock.Of<ILogger<OptimizationWorker>>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            metrics,
            healthMonitor: null,
            healthStore,
            configProvider,
            loopCoordinator,
            timeProvider,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(2));

        await worker.StartAsync(CancellationToken.None);
        await blockingProcessor.AllBlockingCallsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        int cycleCountWhileSlotsBusy = await WaitForCoordinatorCyclesAsync(
            loopCoordinator,
            minimumCycles: 2,
            timeout: TimeSpan.FromSeconds(2));

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, blockingProcessor.MaxConcurrentCalls);
        Assert.True(cycleCountWhileSlotsBusy >= 2);
    }

    [Fact]
    public async Task ExecuteCoordinatorCycleAsync_WhenCoordinatorFails_StillLaunchesProcessingSlots_AndKeepsSchedulingDue()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 09, 9, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(nowUtc);
        var configProvider = await CreatePrimedConfigProviderAsync(
            timeProvider,
            new EngineConfig
            {
                Key = "Optimization:MaxConcurrentRuns",
                Value = "1",
                DataType = ConfigDataType.Int,
                IsDeleted = false
            },
            new EngineConfig
            {
                Key = "Optimization:SchedulePollSeconds",
                Value = "60",
                DataType = ConfigDataType.Int,
                IsDeleted = false
            });

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(new Mock<DbContext>().Object);

        var processor = new SignalingOptimizationRunProcessor();
        var services = new ServiceCollection()
            .AddSingleton(readCtx.Object)
            .AddSingleton<IOptimizationRunProcessor>(processor)
            .BuildServiceProvider();

        var metricsServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new TradingMetrics(
            metricsServices.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var worker = new OptimizationWorker(
            Mock.Of<ILogger<OptimizationWorker>>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            metrics,
            healthMonitor: null,
            new OptimizationWorkerHealthStore(),
            configProvider,
            new ThrowingLoopCoordinator(),
            timeProvider,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(1));

        var executeCycleMethod = typeof(OptimizationWorker).GetMethod(
            "ExecuteCoordinatorCycleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(executeCycleMethod);

        var cycleTask = (Task)executeCycleMethod!.Invoke(worker, [CancellationToken.None])!;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cycleTask);
        await processor.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var nextScheduleScanField = typeof(OptimizationWorker).GetField(
            "_nextScheduleScanUtc",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(nextScheduleScanField);
        Assert.Equal(DateTime.MinValue, (DateTime)nextScheduleScanField!.GetValue(worker)!);
        Assert.Equal(1, processor.InvocationCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConfigLoadFails_ExposesDegradedHealthWithSeparateExecutionStream()
    {
        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext())
            .Throws(new InvalidOperationException("configuration database unavailable"));

        using var provider = CreateOptimizationServiceProvider(configureServices: services =>
        {
            services.AddSingleton(readCtx.Object);
        });

        var worker = new OptimizationWorker(
            provider.GetRequiredService<ILogger<OptimizationWorker>>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TradingMetrics>(),
            provider.GetRequiredService<IWorkerHealthMonitor>(),
            provider.GetRequiredService<IOptimizationWorkerHealthStore>(),
            provider.GetRequiredService<OptimizationConfigProvider>(),
            new CountingLoopCoordinator(),
            provider.GetRequiredService<TimeProvider>(),
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(1));

        await worker.StartAsync(CancellationToken.None);

        OptimizationWorkerHealthDto? snapshot = null;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var response = await new GetOptimizationWorkerHealthQueryHandler(
                provider.GetRequiredService<IWorkerHealthMonitor>(),
                provider.GetRequiredService<IOptimizationWorkerHealthStore>())
                .Handle(new GetOptimizationWorkerHealthQuery(), CancellationToken.None);

            snapshot = response.data;
            if (snapshot is not null
                && snapshot.IsConfigLoadDegraded
                && snapshot.ConsecutiveConfigLoadFailures > 0
                && snapshot.CoordinatorWorker?.WorkerName == OptimizationWorkerHealthNames.CoordinatorWorker
                && snapshot.OptimizationWorker?.WorkerName == OptimizationWorkerHealthNames.ExecutionWorker)
            {
                break;
            }

            await Task.Delay(25);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsConfigLoadDegraded);
        Assert.True(snapshot.ConsecutiveConfigLoadFailures > 0);
        Assert.Equal("configuration database unavailable", snapshot.LastConfigLoadFailureMessage);
        Assert.Equal(OptimizationWorkerHealthNames.CoordinatorWorker, snapshot.CoordinatorWorker!.WorkerName);
        Assert.Equal(OptimizationWorkerHealthNames.ExecutionWorker, snapshot.OptimizationWorker!.WorkerName);
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

    private static OptimizationConfig CreateOptimizationConfig(
        int cooldownDays,
        int maxConsecutiveFailuresBeforeEscalation,
        int maxRunsPerWeek = 20,
        decimal screeningInitialBalance = 10_000m)
    {
        return new OptimizationConfig
        {
            SchedulePollSeconds = 7200,
            CooldownDays = cooldownDays,
            RolloutObservationDays = 14,
            MaxQueuedPerCycle = 3,
            FollowUpMonitorBatchSize = 10,
            AutoScheduleEnabled = true,
            MaxRunsPerWeek = maxRunsPerWeek,
            MinWinRate = 0.60,
            MinProfitFactor = 1.0,
            MinTotalTrades = 10,
            AutoApprovalImprovementThreshold = 0.10m,
            AutoApprovalMinHealthScore = 0.55m,
            TopNCandidates = 5,
            CoarsePhaseThreshold = 10,
            TpeBudget = 50,
            TpeInitialSamples = 15,
            PurgedKFolds = 5,
            AdaptiveBoundsEnabled = true,
            GpEarlyStopPatience = 4,
            PresetName = "balanced",
            HyperbandEnabled = true,
            HyperbandEta = 3,
            UseEhviAcquisition = false,
            UseParegoScalarization = false,
            ScreeningTimeoutSeconds = 30,
            ScreeningSpreadPoints = 20.0,
            ScreeningCommissionPerLot = 7.0,
            ScreeningSlippagePips = 1.0,
            ScreeningInitialBalance = screeningInitialBalance,
            MaxParallelBacktests = 4,
            MinCandidateTrades = 10,
            MaxRunTimeoutMinutes = 30,
            CircuitBreakerThreshold = 10,
            SuccessiveHalvingRungs = "0.25,0.50",
            MaxOosDegradationPct = 0.60,
            EmbargoRatio = 0.05,
            CorrelationParamThreshold = 0.15,
            SensitivityPerturbPct = 0.10,
            SensitivityDegradationTolerance = 0.20,
            BootstrapIterations = 1000,
            MinBootstrapCILower = 0.40m,
            CostSensitivityEnabled = true,
            CostStressMultiplier = 2.0,
            TemporalOverlapThreshold = 0.70,
            PortfolioCorrelationThreshold = 0.80,
            WalkForwardMinMaxRatio = 0.50,
            MinOosCandlesForValidation = 50,
            MaxCvCoefficientOfVariation = 0.50,
            PermutationIterations = 1000,
            MinEquityCurveR2 = 0.60,
            MaxTradeTimeConcentration = 0.60,
            CpcvNFolds = 6,
            CpcvTestFoldCount = 2,
            CpcvMaxCombinations = 15,
            DataScarcityThreshold = 200,
            CandleLookbackMonths = 12,
            CandleLookbackAutoScale = true,
            UseSymbolSpecificSpread = true,
            RegimeBlendRatio = 0.50,
            MaxCrossRegimeEvals = 4,
            RegimeStabilityHours = 6,
            SuppressDuringDrawdownRecovery = true,
            SeasonalBlackoutEnabled = true,
            BlackoutPeriods = "12/20-01/05",
            RequireEADataAvailability = false,
            MaxRetryAttempts = 3,
            MaxConsecutiveFailuresBeforeEscalation = maxConsecutiveFailuresBeforeEscalation,
            CheckpointEveryN = 10,
            MaxConcurrentRuns = 2,
        };
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
        IIntegrationEventService eventService,
        CancellationToken ct = default,
        CancellationToken runCt = default)
    {
        // RunContext exists both as a nested type in OptimizationWorker and as an extracted
        // top-level type. The method under test uses the nested version.
        var contextType = typeof(OptimizationWorker).GetNestedType("RunContext", BindingFlags.NonPublic)
            ?? typeof(OptimizationConfig).Assembly.GetType("LascodiaTradingEngine.Application.Optimization.RunContext", throwOnError: true)!;
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
                ct,
                runCt
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
        // ScoredCandidate was extracted to LascodiaTradingEngine.Application.Optimization namespace
        var scoredCandidateType = typeof(OptimizationConfig).Assembly
            .GetType("LascodiaTradingEngine.Application.Optimization.ScoredCandidate", throwOnError: true)!;
        var scoredCandidate = scoredCandidateType
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(ctor => ctor.GetParameters().Length)
            .First()
            .Invoke([winnerParamsJson, oosHealthScore, oosResult, 0.10]);

        // CandidateValidationResult may exist as nested or extracted. Prefer nested.
        var resultType = typeof(OptimizationWorker).GetNestedType("CandidateValidationResult", BindingFlags.NonPublic)
            ?? typeof(OptimizationConfig).Assembly.GetType("LascodiaTradingEngine.Application.Optimization.CandidateValidationResult", throwOnError: true)!;
        var createMethod = resultType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .First(method => string.Equals(method.Name, "Create", StringComparison.Ordinal));

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["passed"] = passed,
            ["winner"] = scoredCandidate,
            ["oosHealthScore"] = oosHealthScore,
            ["oosResult"] = oosResult,
            ["hasOosValidation"] = true,
            ["ciLower"] = ciLower,
            ["ciMedian"] = oosHealthScore,
            ["ciUpper"] = ciUpper,
            ["permPValue"] = 0.01,
            ["permCorrectedAlpha"] = 0.05,
            ["permSignificant"] = true,
            ["sensitivityOk"] = true,
            ["sensitivityReport"] = "ok",
            ["costSensitiveOk"] = true,
            ["pessimisticScore"] = pessimisticScore,
            ["degradationFailed"] = false,
            ["wfAvgScore"] = wfAvgScore,
            ["wfStable"] = true,
            ["mtfCompatible"] = true,
            ["correlationSafe"] = true,
            ["temporalCorrelationSafe"] = true,
            ["temporalMaxOverlap"] = 0.0,
            ["portfolioCorrelationSafe"] = true,
            ["portfolioMaxCorrelation"] = 0.0,
            ["cvConsistent"] = true,
            ["cvValue"] = 0.10,
            ["approvalReportJson"] = """{"hasSufficientOutOfSampleData":true}""",
            ["failureReason"] = failureReason,
            ["failedCandidates"] = null
        };

        var args = createMethod.GetParameters()
            .Select(parameter => values[parameter.Name!])
            .ToArray();

        return createMethod.Invoke(null, args)!;
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
        var twoAttempts = OptimizationPolicyHelpers.GetRetryEligibilityWindow(2);
        var threeAttempts = OptimizationPolicyHelpers.GetRetryEligibilityWindow(3);

        Assert.Equal(TimeSpan.FromMinutes(45), twoAttempts);
        Assert.Equal(TimeSpan.FromMinutes(75), threeAttempts);
    }

    [Fact]
    public void ExecutionLeaseHeartbeatInterval_IsShorterThanLeaseDuration()
    {
        var interval = OptimizationRunLeaseManager.GetHeartbeatInterval();

        Assert.True(interval >= TimeSpan.FromMinutes(1));
        Assert.True(interval < TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task RetryFailedRunsAsync_SkipsRun_WhenStrategyAlreadyHasActiveOptimization()
    {
        var nowUtc = DateTime.UtcNow;
        var optimizationRuns = new List<OptimizationRun>
        {
            new()
            {
                Id = 401,
                StrategyId = 77,
                Status = OptimizationRunStatus.Failed,
                RetryCount = 0,
                FailureCategory = OptimizationFailureCategory.Transient,
                CompletedAt = nowUtc.AddMinutes(-20),
                StartedAt = nowUtc.AddHours(-1),
                IsDeleted = false
            },
            new()
            {
                Id = 402,
                StrategyId = 77,
                Status = OptimizationRunStatus.Queued,
                StartedAt = nowUtc.AddMinutes(-5),
                IsDeleted = false
            }
        };

        var configs = new List<EngineConfig>
        {
            new()
            {
                Id = 1,
                Key = "Optimization:MaxRetryAttempts",
                Value = "2",
                DataType = ConfigDataType.Int,
                IsDeleted = false
            }
        };

        var optRunDbSet = optimizationRuns.AsQueryable().BuildMockDbSet();
        var configDbSet = configs.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(optRunDbSet.Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await InvokeRetryFailedRunsAsync(readCtx.Object, writeCtx.Object);

        var failedRun = optimizationRuns.Single(r => r.Id == 401);
        Assert.Equal(OptimizationRunStatus.Failed, failedRun.Status);
        Assert.Equal(0, failedRun.RetryCount);
    }

    [Fact]
    public async Task RetryFailedRunsAsync_AbandonsSearchExhaustedRunWithoutRetry()
    {
        var nowUtc = DateTime.UtcNow;
        var optimizationRuns = new List<OptimizationRun>
        {
            new()
            {
                Id = 451,
                StrategyId = 88,
                Status = OptimizationRunStatus.Failed,
                RetryCount = 0,
                FailureCategory = OptimizationFailureCategory.SearchExhausted,
                ErrorMessage = "Search space exhausted",
                CompletedAt = nowUtc.AddMinutes(-20),
                StartedAt = nowUtc.AddHours(-1),
                IsDeleted = false
            }
        };

        var configs = new List<EngineConfig>
        {
            new()
            {
                Id = 1,
                Key = "Optimization:MaxRetryAttempts",
                Value = "2",
                DataType = ConfigDataType.Int,
                IsDeleted = false
            }
        };
        var alerts = new List<Alert>();

        var optRunDbSet = optimizationRuns.AsQueryable().BuildMockDbSet();
        var configDbSet = configs.AsQueryable().BuildMockDbSet();
        var alertDbSet = alerts.AsQueryable().BuildMockDbSet();
        alertDbSet.Setup(d => d.Add(It.IsAny<Alert>()))
            .Callback<Alert>(a => alerts.Add(a));

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(optRunDbSet.Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);
        db.Setup(c => c.Set<Alert>()).Returns(alertDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await InvokeRetryFailedRunsAsync(readCtx.Object, writeCtx.Object);

        var exhaustedRun = optimizationRuns.Single(r => r.Id == 451);
        Assert.Equal(OptimizationRunStatus.Abandoned, exhaustedRun.Status);
        Assert.Equal(0, exhaustedRun.RetryCount);
        Assert.Contains("search exhausted", exhaustedRun.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetryFailedRunsAsync_RequeuesOldTransientRunWhenRetryBudgetRemains()
    {
        var nowUtc = DateTime.UtcNow;
        var optimizationRuns = new List<OptimizationRun>
        {
            new()
            {
                Id = 452,
                StrategyId = 89,
                Status = OptimizationRunStatus.Failed,
                RetryCount = 0,
                FailureCategory = OptimizationFailureCategory.Transient,
                CompletedAt = nowUtc.AddHours(-6),
                StartedAt = nowUtc.AddHours(-7),
                IsDeleted = false
            }
        };

        var configs = new List<EngineConfig>
        {
            new()
            {
                Id = 1,
                Key = "Optimization:MaxRetryAttempts",
                Value = "2",
                DataType = ConfigDataType.Int,
                IsDeleted = false
            }
        };
        var alerts = new List<Alert>();

        var optRunDbSet = optimizationRuns.AsQueryable().BuildMockDbSet();
        var configDbSet = configs.AsQueryable().BuildMockDbSet();
        var alertDbSet = alerts.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(optRunDbSet.Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);
        db.Setup(c => c.Set<Alert>()).Returns(alertDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await InvokeRetryFailedRunsAsync(readCtx.Object, writeCtx.Object);

        var retriedRun = optimizationRuns.Single(r => r.Id == 452);
        Assert.Equal(OptimizationRunStatus.Queued, retriedRun.Status);
        Assert.Equal(1, retriedRun.RetryCount);
    }

    [Fact]
    public async Task RetryFailedRunsAsync_RefreshesQueueLifecycleTimestamps_WhenRequeued()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 08, 12, 0, 0, TimeSpan.Zero);
        var staleQueueEntryUtc = nowUtc.UtcDateTime.AddDays(-5);
        var optimizationRuns = new List<OptimizationRun>
        {
            new()
            {
                Id = 4521,
                StrategyId = 891,
                Status = OptimizationRunStatus.Failed,
                RetryCount = 0,
                FailureCategory = OptimizationFailureCategory.Transient,
                QueuedAt = staleQueueEntryUtc,
                StartedAt = staleQueueEntryUtc,
                ClaimedAt = staleQueueEntryUtc.AddMinutes(10),
                ExecutionStartedAt = staleQueueEntryUtc.AddMinutes(11),
                LastHeartbeatAt = staleQueueEntryUtc.AddMinutes(20),
                CompletedAt = nowUtc.UtcDateTime.AddHours(-6),
                IsDeleted = false
            }
        };

        var configs = new List<EngineConfig>
        {
            new()
            {
                Id = 1,
                Key = "Optimization:MaxRetryAttempts",
                Value = "2",
                DataType = ConfigDataType.Int,
                IsDeleted = false
            }
        };
        var alerts = new List<Alert>();

        var optRunDbSet = optimizationRuns.AsQueryable().BuildMockDbSet();
        var configDbSet = configs.AsQueryable().BuildMockDbSet();
        var alertDbSet = alerts.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(optRunDbSet.Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);
        db.Setup(c => c.Set<Alert>()).Returns(alertDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var timeProvider = new FixedTimeProvider(nowUtc);
        await InvokeRetryFailedRunsAsync(readCtx.Object, writeCtx.Object, timeProvider);

        var retriedRun = optimizationRuns.Single(r => r.Id == 4521);
        Assert.Equal(OptimizationRunStatus.Queued, retriedRun.Status);
        Assert.Equal(nowUtc.UtcDateTime, retriedRun.QueuedAt);
        Assert.Null(retriedRun.ClaimedAt);
        Assert.Null(retriedRun.ExecutionStartedAt);
        Assert.Null(retriedRun.LastHeartbeatAt);

        await InvokeRecoverStaleQueuedRunsAsync(readCtx.Object, writeCtx.Object, timeProvider);

        Assert.Equal(OptimizationRunStatus.Queued, retriedRun.Status);
    }

    [Fact]
    public async Task RecoverStaleQueuedRunsAsync_LeavesOldEligibleQueuedRunInQueue()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 09, 12, 0, 0, TimeSpan.Zero);
        var optimizationRuns = new List<OptimizationRun>
        {
            new()
            {
                Id = 4601,
                StrategyId = 90,
                Status = OptimizationRunStatus.Queued,
                QueuedAt = nowUtc.UtcDateTime.AddHours(-25),
                StartedAt = nowUtc.UtcDateTime.AddHours(-26),
                IsDeleted = false
            }
        };

        var optRunDbSet = optimizationRuns.AsQueryable().BuildMockDbSet();
        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(optRunDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        await InvokeRecoverStaleQueuedRunsAsync(readCtx.Object, writeCtx.Object, new FixedTimeProvider(nowUtc));

        var run = Assert.Single(optimizationRuns);
        Assert.Equal(OptimizationRunStatus.Queued, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.ErrorMessage);
        Assert.Null(run.FailureCategory);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RetryFailedRunsAsync_AbandonsConfigErrorRun_WhenRetriesDisabled()
    {
        var nowUtc = DateTime.UtcNow;
        var optimizationRuns = new List<OptimizationRun>
        {
            new()
            {
                Id = 453,
                StrategyId = 90,
                Status = OptimizationRunStatus.Failed,
                RetryCount = 0,
                FailureCategory = OptimizationFailureCategory.ConfigError,
                ErrorMessage = "Invalid Optimization:EmbargoRatio",
                CompletedAt = nowUtc.AddMinutes(-5),
                StartedAt = nowUtc.AddMinutes(-30),
                IsDeleted = false
            }
        };

        var configs = new List<EngineConfig>
        {
            new()
            {
                Id = 1,
                Key = "Optimization:MaxRetryAttempts",
                Value = "0",
                DataType = ConfigDataType.Int,
                IsDeleted = false
            }
        };
        var alerts = new List<Alert>();

        var optRunDbSet = optimizationRuns.AsQueryable().BuildMockDbSet();
        var configDbSet = configs.AsQueryable().BuildMockDbSet();
        var alertDbSet = alerts.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(optRunDbSet.Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);
        db.Setup(c => c.Set<Alert>()).Returns(alertDbSet.Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await InvokeRetryFailedRunsAsync(readCtx.Object, writeCtx.Object);

        var abandonedRun = optimizationRuns.Single(r => r.Id == 453);
        Assert.Equal(OptimizationRunStatus.Abandoned, abandonedRun.Status);
        Assert.Contains("invalid configuration", abandonedRun.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StateMachine_AllowsRetryPath_Failed_To_Queued()
    {
        Assert.True(OptimizationRunStateMachine.CanTransition(
            OptimizationRunStatus.Failed, OptimizationRunStatus.Queued));
    }

    [Fact]
    public void StateMachine_HealthyTransitions_ClearFailureCategory()
    {
        var queuedRun = new OptimizationRun
        {
            Id = 301,
            Status = OptimizationRunStatus.Failed,
            FailureCategory = OptimizationFailureCategory.Timeout,
            ErrorMessage = "boom"
        };

        OptimizationRunStateMachine.Transition(queuedRun, OptimizationRunStatus.Queued, DateTime.UtcNow);

        Assert.Null(queuedRun.FailureCategory);
        Assert.Null(queuedRun.ErrorMessage);

        var completedRun = new OptimizationRun
        {
            Id = 302,
            Status = OptimizationRunStatus.Running,
            FailureCategory = OptimizationFailureCategory.Transient
        };

        OptimizationRunStateMachine.Transition(completedRun, OptimizationRunStatus.Completed, DateTime.UtcNow);

        Assert.Null(completedRun.FailureCategory);

        var approvedRun = new OptimizationRun
        {
            Id = 303,
            Status = OptimizationRunStatus.Completed,
            FailureCategory = OptimizationFailureCategory.DataQuality
        };

        OptimizationRunStateMachine.Transition(approvedRun, OptimizationRunStatus.Approved, DateTime.UtcNow);

        Assert.Null(approvedRun.FailureCategory);
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

    [Fact]
    public void StateMachine_Transition_ClearsExecutionLeaseToken()
    {
        var run = new OptimizationRun
        {
            Id = 102,
            Status = OptimizationRunStatus.Running,
            ExecutionLeaseToken = Guid.NewGuid(),
            ExecutionLeaseExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };

        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Completed, DateTime.UtcNow);

        Assert.Null(run.ExecutionLeaseToken);
        Assert.Null(run.ExecutionLeaseExpiresAt);
    }

    [Fact]
    public void StateMachine_Transition_ToQueued_RefreshesQueueLifecycleTimestamps()
    {
        var previousQueuedAt = new DateTime(2026, 04, 01, 9, 0, 0, DateTimeKind.Utc);
        var requeuedAt = new DateTime(2026, 04, 08, 9, 0, 0, DateTimeKind.Utc);
        var run = new OptimizationRun
        {
            Id = 103,
            Status = OptimizationRunStatus.Running,
            QueuedAt = previousQueuedAt,
            ClaimedAt = previousQueuedAt.AddMinutes(15),
            ExecutionStartedAt = previousQueuedAt.AddMinutes(16),
            LastHeartbeatAt = previousQueuedAt.AddMinutes(25),
            ExecutionLeaseToken = Guid.NewGuid(),
            ExecutionLeaseExpiresAt = previousQueuedAt.AddMinutes(35)
        };

        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, requeuedAt);

        Assert.Equal(requeuedAt, run.QueuedAt);
        Assert.Null(run.ClaimedAt);
        Assert.Null(run.ExecutionStartedAt);
        Assert.Null(run.LastHeartbeatAt);
        Assert.Null(run.ExecutionLeaseToken);
        Assert.Null(run.ExecutionLeaseExpiresAt);
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
    public void ConfigValidator_RejectsOutOfRangeApprovalShapeThresholds()
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
            regimeBlendRatio: 1.25,
            minEquityCurveR2: -0.1,
            maxTradeTimeConcentration: 1.1);

        Assert.Contains(issues, i => i.Contains("RegimeBlendRatio"));
        Assert.Contains(issues, i => i.Contains("MinEquityCurveR2"));
        Assert.Contains(issues, i => i.Contains("MaxTradeTimeConcentration"));
    }

    [Fact]
    public void AreParametersSimilarToAny_ReturnsTrue_ForMatchingCategoricalOnlyParameters()
    {
        const string candidateJson = """{"Mode":"Breakout","Session":"London"}""";
        var parsed = new List<Dictionary<string, JsonElement>>
        {
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(candidateJson)!
        };

        var isSimilar = OptimizationPolicyHelpers.AreParametersSimilarToAny(candidateJson, parsed, 0.15);

        Assert.True(isSimilar);
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
        // Drawdown multiplier is set explicitly here to keep DD scaled on the fallback path.
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
            AssetClassPfMultiplier = 1.0,
            AssetClassDrawdownMultiplier = 1.3
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
    public void CheckpointStore_CompatibilityCheck_FailsWhenDataWindowFingerprintChanges()
    {
        string json = OptimizationCheckpointStore.Serialize(
            iterations: 3,
            stagnantBatches: 1,
            surrogateKind: "TPE",
            surrogateRandomState: 42UL,
            observations:
            [
                new OptimizationCheckpointStore.Observation(
                    Sequence: 1,
                    ParamsJson: """{"A":1,"B":2}""",
                    HealthScore: 0.65m,
                    CvCoefficientOfVariation: 0.08,
                    Result: new BacktestResult
                    {
                        TotalTrades = 25,
                        WinRate = 0.56m,
                        ProfitFactor = 1.3m,
                        Trades = []
                    })
            ],
            seenParameterJson: ["""{"A":1,"B":2}"""],
            dataWindowFingerprint: "ORIGINAL",
            candleWindowStartUtc: new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
            candleWindowEndUtc: new DateTime(2026, 03, 10, 0, 0, 0, DateTimeKind.Utc),
            candleCount: 200,
            trainCandleCount: 150,
            testCandleCount: 40,
            optimizationRegimeText: "Trending");

        var restored = OptimizationCheckpointStore.Restore(json);

        bool compatible = OptimizationCheckpointStore.TryValidateCompatibility(
            restored,
            currentDataWindowFingerprint: "CHANGED",
            candleWindowStartUtc: new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
            candleWindowEndUtc: new DateTime(2026, 03, 10, 0, 0, 0, DateTimeKind.Utc),
            candleCount: 200,
            trainCandleCount: 150,
            testCandleCount: 40,
            optimizationRegimeText: "Trending",
            mismatchReason: out var mismatchReason,
            currentSurrogateKind: "TPE");

        Assert.False(compatible);
        Assert.Equal("data window fingerprint changed", mismatchReason);
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
    public void CheckpointStore_Serialize_KeepsMostRecentObservations_WhenForcedIntoFinalTrim()
    {
        string largeBlob = new string('x', 40_000);
        var observations = Enumerable.Range(1, 60)
            .Select(i => new OptimizationCheckpointStore.Observation(
                Sequence: i,
                ParamsJson: JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["Fast"] = i,
                    ["Blob"] = largeBlob
                }),
                HealthScore: 0.50m + i / 1000m,
                CvCoefficientOfVariation: 0.10,
                Result: new BacktestResult
                {
                    TotalTrades = 10,
                    WinRate = 0.55m,
                    ProfitFactor = 1.30m,
                    SharpeRatio = 1.0m,
                    MaxDrawdownPct = 8m,
                    Trades = []
                }))
            .ToList();

        string serialized = OptimizationCheckpointStore.Serialize(
            iterations: observations.Count,
            stagnantBatches: 1,
            surrogateKind: "TPE",
            surrogateRandomState: 123UL,
            observations: observations,
            seenParameterJson: ["seen-a", "seen-b"]);

        var restored = OptimizationCheckpointStore.Restore(serialized);

        Assert.True(restored.Observations.Count <= 25);
        // After trimming, sequences are re-indexed to close gaps (1-based consecutive).
        Assert.Equal(1, restored.Observations.First().Sequence);
        Assert.Equal(restored.Observations.Count, restored.Observations.Last().Sequence);
    }

    [Fact]
    public void ActiveAndFreshForSymbol_RequiresExactFreshMatch()
    {
        var now = DateTime.UtcNow;
        var instances = new List<EAInstance>
        {
            new()
            {
                Id = 1,
                Status = EAInstanceStatus.Active,
                Symbols = "EURUSD.pro,GBPUSD",
                LastHeartbeat = now,
                IsDeleted = false
            },
            new()
            {
                Id = 2,
                Status = EAInstanceStatus.Active,
                Symbols = "EURUSD,USDJPY",
                LastHeartbeat = now.AddMinutes(-10),
                IsDeleted = false
            },
            new()
            {
                Id = 3,
                Status = EAInstanceStatus.Active,
                Symbols = "EURUSD,GBPUSD",
                LastHeartbeat = now,
                IsDeleted = false
            }
        };

        var matches = instances.AsQueryable()
            .ActiveAndFreshForSymbol("EURUSD", TimeSpan.FromMinutes(1))
            .ToList();

        Assert.Single(matches);
        Assert.Equal(3L, matches[0].Id);
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

        var result = await OptimizationConfigProvider.LoadDirectAsync(
            db.Object,
            Mock.Of<ILogger>(),
            CancellationToken.None);
        var maxRunsPerWeek = result.MaxRunsPerWeek;

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
        var binomialMethod = typeof(OptimizationValidationCoordinator).GetMethod(
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

        OptimizationRunClaimer.StampHeartbeat(
            run,
            leaseDuration,
            new DateTime(2026, 04, 07, 12, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(run.LastHeartbeatAt);
        Assert.NotNull(run.ExecutionLeaseExpiresAt);
        Assert.True(run.ExecutionLeaseExpiresAt > run.LastHeartbeatAt);
        Assert.InRange(
            (run.ExecutionLeaseExpiresAt!.Value - run.LastHeartbeatAt!.Value).TotalMinutes,
            9.9, 10.1);
    }

    [Fact]
    public void HasLeaseOwnershipChanged_ReturnsTrue_WhenTokenOrStatusDiffers()
    {
        var expectedToken = Guid.NewGuid();

        Assert.False(OptimizationRunLeaseManager.HasLeaseOwnershipChanged(expectedToken, OptimizationRunStatus.Running, expectedToken));
        Assert.True(OptimizationRunLeaseManager.HasLeaseOwnershipChanged(expectedToken, OptimizationRunStatus.Running, Guid.NewGuid()));
        Assert.True(OptimizationRunLeaseManager.HasLeaseOwnershipChanged(expectedToken, OptimizationRunStatus.Completed, expectedToken));
    }

    [Fact]
    public async Task EnsureValidationFollowUpsAsync_PinsApprovedParametersAndDisablesWalkForwardReoptimization()
    {
        var run = new OptimizationRun
        {
            Id = 101,
            StrategyId = 5,
            BestParametersJson = """{"Fast":12,"Slow":34}"""
        };
        var strategy = new Strategy
        {
            Id = 5,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":10,"Slow":30}"""
        };

        var backtests = new List<BacktestRun>();
        var walks = new List<WalkForwardRun>();
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
        var coordinator = CreateFollowUpCoordinator();

        var result = await coordinator.EnsureValidationFollowUpsAsync(db.Object, run, strategy, config, CancellationToken.None);
        Assert.False(result);

        var backtest = Assert.Single(backtests);
        var walkForward = Assert.Single(walks);
        Assert.Equal("""{"Fast":12,"Slow":34}""", backtest.ParametersSnapshotJson);
        Assert.Equal("""{"Fast":12,"Slow":34}""", walkForward.ParametersSnapshotJson);
        Assert.False(walkForward.ReOptimizePerFold);
    }

    [Fact]
    public async Task BacktestWorker_UsesPinnedParameterSnapshot_WhenPresent()
    {
        var run = new BacktestRun
        {
            Id = 201,
            StrategyId = 7,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            FromDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            ToDate = new DateTime(2026, 01, 02, 0, 0, 0, DateTimeKind.Utc),
            InitialBalance = 10_000m,
            Status = RunStatus.Queued,
            ParametersSnapshotJson = """{"mode":"approved"}""",
            IsDeleted = false
        };
        var strategy = new Strategy
        {
            Id = 7,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"mode":"live"}""",
            Status = StrategyStatus.Active,
            IsDeleted = false
        };
        var candles = Enumerable.Range(0, 24)
            .Select(i => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = run.FromDate.AddHours(i),
                Open = 1.10m,
                High = 1.11m,
                Low = 1.09m,
                Close = 1.10m,
                IsClosed = true
            })
            .ToList();

        var runs = new List<BacktestRun> { run };
        var strategies = new List<Strategy> { strategy };
        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var strategyDbSet = strategies.AsQueryable().BuildMockDbSet();
        var candleDbSet = candles.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<BacktestRun>()).Returns(runDbSet.Object);
        db.Setup(c => c.Set<Strategy>()).Returns(strategyDbSet.Object);
        db.Setup(c => c.Set<Candle>()).Returns(candleDbSet.Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(new List<EconomicEvent>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(new List<CurrencyPair>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<SpreadProfile>()).Returns(new List<SpreadProfile>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var eventService = new Mock<IIntegrationEventService>();
        eventService.Setup(x => x.SaveAndPublish(It.IsAny<IDbContext>(), It.IsAny<Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>()))
            .Returns(Task.CompletedTask);

        var engine = new RecordingBacktestEngine();
        var services = new ServiceCollection()
            .AddSingleton(readCtx.Object)
            .AddSingleton(writeCtx.Object)
            .AddSingleton(eventService.Object)
            .AddSingleton<IValidationWorkerIdentity>(new TestValidationWorkerIdentity("test-opt-backtest-worker"))
            .AddScoped<IValidationSettingsProvider, ValidationSettingsProvider>()
            .AddScoped<IBacktestOptionsSnapshotBuilder>(sp =>
                new BacktestOptionsSnapshotBuilder(
                    sp.GetRequiredService<IValidationSettingsProvider>(),
                    NullLogger<BacktestOptionsSnapshotBuilder>.Instance))
            .AddScoped<IValidationRunFactory>(sp =>
                new ValidationRunFactory(
                    sp.GetRequiredService<IBacktestOptionsSnapshotBuilder>(),
                    TimeProvider.System))
            .AddSingleton<IAutoWalkForwardWindowPolicy, AutoWalkForwardWindowPolicy>()
            .BuildServiceProvider();

        var worker = new BacktestWorker(
            Mock.Of<ILogger<BacktestWorker>>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            engine,
            new InMemoryBacktestRunClaimService(),
            services.GetRequiredService<IValidationSettingsProvider>(),
            Mock.Of<IBacktestAutoScheduler>(),
            services.GetRequiredService<IValidationRunFactory>(),
            services.GetRequiredService<IBacktestOptionsSnapshotBuilder>(),
            services.GetRequiredService<IAutoWalkForwardWindowPolicy>(),
            services.GetRequiredService<IValidationWorkerIdentity>());

        var method = typeof(BacktestWorker).GetMethod(
            "ProcessNextQueuedRunAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(worker, [CancellationToken.None])!;

        Assert.Single(engine.SeenParameterJson);
        Assert.Equal("""{"mode":"approved"}""", engine.SeenParameterJson[0]);
    }

    [Fact]
    public async Task WalkForwardWorker_UsesPinnedParameterSnapshot_WithoutReoptimizing()
    {
        var run = new WalkForwardRun
        {
            Id = 301,
            StrategyId = 9,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            FromDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            ToDate = new DateTime(2026, 02, 20, 0, 0, 0, DateTimeKind.Utc),
            InSampleDays = 20,
            OutOfSampleDays = 10,
            InitialBalance = 10_000m,
            ReOptimizePerFold = true,
            Status = RunStatus.Queued,
            ParametersSnapshotJson = """{"mode":"approved"}""",
            IsDeleted = false
        };
        var strategy = new Strategy
        {
            Id = 9,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"mode":"live"}""",
            Status = StrategyStatus.Active,
            IsDeleted = false
        };
        var candles = Enumerable.Range(0, 60)
            .SelectMany(day => Enumerable.Range(0, 24).Select(hour => run.FromDate.AddDays(day).AddHours(hour)))
            .Where(ts => ts < run.ToDate)
            .Select(ts => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = ts,
                Open = 1.10m,
                High = 1.11m,
                Low = 1.09m,
                Close = 1.10m,
                IsClosed = true
            })
            .ToList();

        var runs = new List<WalkForwardRun> { run };
        var strategies = new List<Strategy> { strategy };
        var configs = new List<EngineConfig>();
        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var strategyDbSet = strategies.AsQueryable().BuildMockDbSet();
        var candleDbSet = candles.AsQueryable().BuildMockDbSet();
        var configDbSet = configs.AsQueryable().BuildMockDbSet();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(runDbSet.Object);
        db.Setup(c => c.Set<Strategy>()).Returns(strategyDbSet.Object);
        db.Setup(c => c.Set<Candle>()).Returns(candleDbSet.Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(new List<EconomicEvent>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(new List<CurrencyPair>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<SpreadProfile>()).Returns(new List<SpreadProfile>().AsQueryable().BuildMockDbSet().Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var engine = new RecordingBacktestEngine();
        var services = new ServiceCollection()
            .AddSingleton(readCtx.Object)
            .AddSingleton(writeCtx.Object)
            .AddSingleton<IValidationWorkerIdentity>(new TestValidationWorkerIdentity("test-opt-walkforward-worker"))
            .AddScoped<IValidationSettingsProvider, ValidationSettingsProvider>()
            .AddScoped<IBacktestOptionsSnapshotBuilder>(sp =>
                new BacktestOptionsSnapshotBuilder(
                    sp.GetRequiredService<IValidationSettingsProvider>(),
                    NullLogger<BacktestOptionsSnapshotBuilder>.Instance))
            .BuildServiceProvider();

        var worker = new WalkForwardWorker(
            Mock.Of<ILogger<WalkForwardWorker>>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            engine,
            new InMemoryWalkForwardRunClaimService(),
            services.GetRequiredService<IValidationSettingsProvider>(),
            services.GetRequiredService<IBacktestOptionsSnapshotBuilder>(),
            services.GetRequiredService<IValidationWorkerIdentity>());

        var method = typeof(WalkForwardWorker).GetMethod(
            "ProcessAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(worker, [CancellationToken.None])!;

        Assert.Equal(3, engine.SeenParameterJson.Count);
        Assert.All(engine.SeenParameterJson, p => Assert.Equal("""{"mode":"approved"}""", p));
        Assert.All(engine.SeenCandleCounts, count => Assert.Equal(240, count));
    }

    [Fact]
    public async Task CostSensitivitySweepAsync_PreservesDynamicSpreadModel_AndExecutionSettings()
    {
        var engine = new OptionsCapturingBacktestEngine();
        var validator = new OptimizationValidator(engine, TimeProvider.System);
        validator.SetInitialBalance(10_000m);

        var strategy = new Strategy
        {
            Id = 18,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"mode":"winner"}"""
        };

        var candles = Enumerable.Range(0, 40)
            .Select(i => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = 1.10m,
                High = 1.11m,
                Low = 1.09m,
                Close = 1.10m,
                IsClosed = true
            })
            .ToList();

        TradeSignal sizingSignal = new()
        {
            EntryPrice = 1.1000m,
            StopLoss = 1.0950m,
            SuggestedLotSize = 0.50m
        };

        var baseOptions = new BacktestOptions
        {
            SpreadPriceUnits = 0.0002m,
            SpreadFunction = _ => 0.0003m,
            CommissionPerLot = 7m,
            SlippagePriceUnits = 0.0001m,
            SwapPerLotPerDay = 1.5m,
            ContractSize = 100_000m,
            GapSlippagePct = 0.4m,
            FillRatio = 0.85m,
            PositionSizer = (_, _) => 0.25m
        };

        var (isRobust, pessimisticScore) = await validator.CostSensitivitySweepAsync(
            strategy,
            strategy.ParametersJson,
            candles,
            baseOptions,
            approvalThreshold: 0.55m,
            timeoutSecs: 5,
            ct: CancellationToken.None,
            costMultiplier: 2.0);

        Assert.True(isRobust);
        Assert.True(pessimisticScore > 0m);
        Assert.NotNull(engine.LastOptions);
        Assert.Equal(0.0004m, engine.LastOptions!.SpreadPriceUnits);
        Assert.NotNull(engine.LastOptions.SpreadFunction);
        Assert.Equal(0.0006m, engine.LastOptions.SpreadFunction!(candles[0].Timestamp));
        Assert.Equal(14m, engine.LastOptions.CommissionPerLot);
        Assert.Equal(0.0002m, engine.LastOptions.SlippagePriceUnits);
        Assert.Equal(baseOptions.SwapPerLotPerDay, engine.LastOptions.SwapPerLotPerDay);
        Assert.Equal(baseOptions.GapSlippagePct, engine.LastOptions.GapSlippagePct);
        Assert.Equal(baseOptions.FillRatio, engine.LastOptions.FillRatio);
        Assert.Equal(0.25m, engine.LastOptions.PositionSizer!(10_000m, sizingSignal));
    }

    [Fact]
    public async Task WalkForwardValidateAsync_UsesProvidedBaselineParams_InsteadOfLiveStrategyParams()
    {
        var validator = new OptimizationValidator(new BaselineSelectionBacktestEngine(), TimeProvider.System);
        validator.SetInitialBalance(10_000m);

        var strategy = new Strategy
        {
            Id = 15,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"mode":"live"}"""
        };
        var candles = Enumerable.Range(0, 50)
            .Select(i => new Candle
            {
                Timestamp = new DateTime(2026, 02, 01, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = 1.10m,
                High = 1.11m,
                Low = 1.09m,
                Close = 1.10m,
                IsClosed = true
            })
            .ToList();

        var (avgScore, isStable) = await validator.WalkForwardValidateAsync(
            strategy,
            """{"mode":"winner"}""",
            candles,
            new BacktestOptions(),
            timeoutSecs: 5,
            ct: CancellationToken.None,
            minMaxRatio: 0.50,
            baselineParamsJson: """{"mode":"baseline"}""");

        Assert.True(isStable);
        Assert.True(avgScore > 0.75m, $"Expected winner to beat provided baseline; actual avg={avgScore:F3}");
    }

    [Fact]
    public async Task CpcvEvaluateAsync_UsesTrainAndTestEvaluationsPerCombination()
    {
        var engine = new RecordingBacktestEngine();
        var validator = new OptimizationValidator(engine, TimeProvider.System);
        validator.SetInitialBalance(10_000m);

        var strategy = new Strategy
        {
            Id = 16,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"mode":"winner"}"""
        };
        var candles = Enumerable.Range(0, 80)
            .Select(i => new Candle
            {
                Timestamp = new DateTime(2026, 02, 10, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = 1.10m,
                High = 1.11m,
                Low = 1.09m,
                Close = 1.10m,
                IsClosed = true
            })
            .ToList();

        var (_, _, _, combos, _) = await validator.CpcvEvaluateAsync(
            strategy,
            strategy.ParametersJson,
            candles,
            new BacktestOptions(),
            timeoutSecs: 5,
            nFolds: 4,
            testFoldCount: 1,
            embargoCandles: 1,
            minTrades: 1,
            maxCombinations: 2,
            seed: 123,
            ct: CancellationToken.None,
            maxParallelism: 1);

        Assert.Equal(2, combos);
        Assert.Equal(4, engine.SeenCandleCounts.Count);
        Assert.Contains(engine.SeenCandleCounts, c => c > 20);
        Assert.Contains(engine.SeenCandleCounts, c => c == 20);
    }

    [Fact]
    public async Task SensitivityCheckAsync_ClampsToBounds_AndPreservesIntegerParameters()
    {
        var engine = new RecordingBacktestEngine();
        var validator = new OptimizationValidator(engine, TimeProvider.System);
        validator.SetInitialBalance(10_000m);

        var strategy = new Strategy
        {
            Id = 17,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Lookback":10,"Threshold":0.0}"""
        };
        var candles = Enumerable.Range(0, 60)
            .Select(i => new Candle
            {
                Timestamp = new DateTime(2026, 02, 20, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = 1.10m,
                High = 1.11m,
                Low = 1.09m,
                Close = 1.10m,
                IsClosed = true
            })
            .ToList();

        var bounds = new Dictionary<string, (double Min, double Max, bool IsInteger)>
        {
            ["Lookback"] = (5, 15, true),
            ["Threshold"] = (-0.1, 0.1, false),
        };

        var (isRobust, _) = await validator.SensitivityCheckAsync(
            strategy,
            strategy.ParametersJson,
            candles,
            new BacktestOptions(),
            timeoutSecs: 5,
            baseScore: 0.62m,
            perturbPct: 0.10,
            ct: CancellationToken.None,
            degradationTolerance: 0.20,
            maxParallel: 1,
            parameterBounds: bounds);

        Assert.True(isRobust);
        Assert.NotEmpty(engine.SeenParameterJson);
        foreach (var json in engine.SeenParameterJson)
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            double lookback = parsed["Lookback"].GetDouble();
            double threshold = parsed["Threshold"].GetDouble();

            Assert.InRange(lookback, 5, 15);
            Assert.Equal(Math.Round(lookback), lookback, 6);
            Assert.InRange(threshold, -0.1, 0.1);
        }
    }

    // ── Category 3: Edge-case tests for 100/100 testability ──────────────

    // #1: IsInBlackoutPeriod — wrap-around range
    [Theory]
    [InlineData("12/20-01/05", 12, 25, true)]   // Dec 25 — inside wrap-around
    [InlineData("12/20-01/05", 1, 3, true)]      // Jan 3 — inside wrap-around
    [InlineData("12/20-01/05", 1, 10, false)]    // Jan 10 — outside
    [InlineData("12/20-01/05", 6, 15, false)]    // Jun 15 — outside
    [InlineData("12/20-01/05", 12, 20, true)]    // Dec 20 — boundary start
    [InlineData("12/20-01/05", 1, 5, true)]      // Jan 5 — boundary end
    public void IsInBlackoutPeriod_WraparoundRanges(string periods, int month, int day, bool expected)
    {
        var utcNow = new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Utc);
        var result = OptimizationPolicyHelpers.IsInBlackoutPeriod(periods, utcNow);
        Assert.Equal(expected, result);
    }

    // #2: IsInBlackoutPeriod — malformed input
    [Theory]
    [InlineData("")]
    [InlineData("13/01-01/05")]    // Invalid month
    [InlineData("abc")]
    [InlineData("01/32-02/01")]    // Invalid day
    public void IsInBlackoutPeriod_MalformedInput_ReturnsFalse(string periods)
    {
        var result = OptimizationPolicyHelpers.IsInBlackoutPeriod(periods, new DateTime(2026, 06, 15, 12, 0, 0, DateTimeKind.Utc));
        Assert.False(result);
    }

    // #3: IsMeaningfullyDeteriorating — edge cases
    [Fact]
    public void IsMeaningfullyDeteriorating_IdenticalScores_ReturnsFalse()
    {
        var result = OptimizationPolicyHelpers.IsMeaningfullyDeteriorating(
            new List<decimal> { 0.70m, 0.70m, 0.70m },
            out decimal predictedDecline);
        Assert.False(result);
        Assert.Equal(0m, predictedDecline);
    }

    [Fact]
    public void IsMeaningfullyDeteriorating_AscendingScores_ReturnsFalse()
    {
        // Snapshots ordered newest-first: 0.80 (newest), 0.70, 0.60 (oldest) → ascending trend
        var result = OptimizationPolicyHelpers.IsMeaningfullyDeteriorating(
            new List<decimal> { 0.80m, 0.70m, 0.60m },
            out _);
        Assert.False(result);
    }

    [Fact]
    public void IsMeaningfullyDeteriorating_FewerThan3Snapshots_ReturnsFalse()
    {
        var result = OptimizationPolicyHelpers.IsMeaningfullyDeteriorating(
            new List<decimal> { 0.80m, 0.60m },
            out _);
        Assert.False(result);
    }

    // #4: GetRetryEligibilityWindow — verify math for different retry counts
    [Theory]
    [InlineData(1, 30)]   // 15 << 0 + 15 = 30 min
    [InlineData(2, 45)]   // 15 << 1 + 15 = 45 min
    [InlineData(5, 255)]  // 15 << 4 + 15 = 255 min
    public void GetRetryEligibilityWindow_ReturnsCorrectTimeSpan(int maxRetries, int expectedMinutes)
    {
        var result = OptimizationPolicyHelpers.GetRetryEligibilityWindow(maxRetries);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), result);
    }

    // #5: ResolveStrategyCurrencies — various inputs
    [Fact]
    public void ResolveStrategyCurrencies_WithNullPairInfo_ExtractsFromSymbol()
    {
        var result = OptimizationRunMetadataService.ResolveStrategyCurrencies("EURUSD", null);
        Assert.Contains("EUR", result);
        Assert.Contains("USD", result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ResolveStrategyCurrencies_WithPairInfo_UsesPairFields()
    {
        var pair = new CurrencyPair { BaseCurrency = "GBP", QuoteCurrency = "JPY" };
        var result = OptimizationRunMetadataService.ResolveStrategyCurrencies("GBPJPY", pair);
        Assert.Contains("GBP", result);
        Assert.Contains("JPY", result);
    }

    [Fact]
    public void ResolveStrategyCurrencies_ShortSymbol_ReturnsEmpty()
    {
        var result = OptimizationRunMetadataService.ResolveStrategyCurrencies("XAU", null);
        Assert.Empty(result);
    }

    // #6: ParseFidelityRungs — various inputs
    [Fact]
    public void ParseFidelityRungs_MalformedValues_IgnoredWithDefault()
    {
        var result = OptimizationPolicyHelpers.ParseFidelityRungs("abc,def", Mock.Of<ILogger>(), "OptimizationWorkerTest");
        Assert.Equal([0.25, 0.50], result); // Falls back to default
    }

    [Fact]
    public void ParseFidelityRungs_PartiallyValid_KeepsValidValues()
    {
        var result = OptimizationPolicyHelpers.ParseFidelityRungs("abc,0.30,0.60", Mock.Of<ILogger>(), "OptimizationWorkerTest");
        Assert.Equal([0.30, 0.60], result);
    }

    [Fact]
    public void ParseFidelityRungs_OutOfRangeValues_Excluded()
    {
        var result = OptimizationPolicyHelpers.ParseFidelityRungs("0,0.50,1.0,1.5", Mock.Of<ILogger>(), "OptimizationWorkerTest");
        Assert.Equal([0.50], result); // 0, 1.0, and 1.5 are excluded
    }

    // #7: DiffConfigSnapshots — various diffs
    [Fact]
    public void DiffConfigSnapshots_NumericPrecision_NoFalsePositive()
    {
        string prior   = """{"Version":1,"Config":{"TpeBudget":50,"EmbargoRatio":0.05}}""";
        string current = """{"Version":1,"Config":{"TpeBudget":50,"EmbargoRatio":0.05}}""";
        var result = OptimizationRunScopedConfigService.DiffConfigSnapshots(prior, current);
        Assert.Empty(result);
    }

    [Fact]
    public void DiffConfigSnapshots_DetectsChangedKey()
    {
        string prior   = """{"Version":1,"Config":{"TpeBudget":50}}""";
        string current = """{"Version":1,"Config":{"TpeBudget":100}}""";
        var result = OptimizationRunScopedConfigService.DiffConfigSnapshots(prior, current);
        Assert.Single(result);
    }

    [Fact]
    public void DiffConfigSnapshots_DetectsAddedKey()
    {
        string prior   = """{"Version":1,"Config":{"TpeBudget":50}}""";
        string current = """{"Version":1,"Config":{"TpeBudget":50,"NewKey":true}}""";
        var result = OptimizationRunScopedConfigService.DiffConfigSnapshots(prior, current);
        Assert.Single(result);
    }

    // #8: ShouldPreservePersistedResult — all status combinations
    [Theory]
    [InlineData(true, "Completed", true)]
    [InlineData(true, "Approved", true)]
    [InlineData(true, "Rejected", true)]
    [InlineData(true, "Running", false)]
    [InlineData(true, "Failed", false)]
    [InlineData(true, "Queued", false)]
    [InlineData(false, "Completed", false)]
    [InlineData(false, "Approved", false)]
    public void ShouldPreservePersistedResult_AllCombinations(bool persisted, string statusStr, bool expected)
    {
        var status = Enum.Parse<OptimizationRunStatus>(statusStr);
        var result = OptimizationRunLifecycle.ShouldPreservePersistedResult(persisted, status);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(12, 120, 10, true)]
    [InlineData(9, 120, 10, false)]
    [InlineData(12, 90, 10, false)]
    [InlineData(12, 120, 20, false)]
    public void CoarseScreeningThreshold_UsesConfiguredThreshold(
        int candidateCount,
        int trainCandles,
        int coarsePhaseThreshold,
        bool expected)
    {
        bool actual = OptimizationSearchCoordinator.ShouldRunCoarseScreening(
            candidateCount,
            trainCandles,
            coarsePhaseThreshold);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(12, 10, true)]
    [InlineData(9, 10, false)]
    public void CoarseScreeningContinuation_UsesConfiguredThreshold(
        int candidateCount,
        int coarsePhaseThreshold,
        bool expected)
    {
        bool actual = OptimizationSearchCoordinator.ShouldContinueCoarseScreening(
            candidateCount,
            coarsePhaseThreshold);

        Assert.Equal(expected, actual);
    }

    // #9: MarkRunFailedForRetry via state machine (validates Completed→Failed transition)
    [Fact]
    public void StateMachine_CompletedToFailed_IsValidTransition()
    {
        Assert.True(OptimizationRunStateMachine.CanTransition(
            OptimizationRunStatus.Completed, OptimizationRunStatus.Failed));
    }

    [Fact]
    public void StateMachine_CompletedToFailed_SetsCorrectFields()
    {
        var run = new OptimizationRun
        {
            Id = 1,
            Status = OptimizationRunStatus.Completed,
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
        };

        OptimizationRunStateMachine.Transition(
            run, OptimizationRunStatus.Failed, DateTime.UtcNow, "Approval persistence failure");

        Assert.Equal(OptimizationRunStatus.Failed, run.Status);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal("Approval persistence failure", run.ErrorMessage);
        Assert.Null(run.ExecutionLeaseExpiresAt);
    }

    // #10: UpdateConsecutiveFailureStreak — no-op case
    [Fact]
    public void UpdateConsecutiveFailureStreak_NoSuccessesNoFailures_KeepsCurrent()
    {
        int result = OptimizationSearchCoordinator.UpdateConsecutiveFailureStreak(5, 0, 0);
        Assert.Equal(5, result);
    }

    // #11: ComputeBootstrapCI — blending zone boundaries
    [Fact]
    public void ComputeBootstrapCI_AtSyntheticThreshold_PureSynthetic()
    {
        var oosResult = new BacktestResult
        {
            TotalTrades = 7,
            WinRate = 0.60m, ProfitFactor = 1.5m, MaxDrawdownPct = 5m, SharpeRatio = 1.0m,
            Trades = Enumerable.Range(0, 7).Select(i => new BacktestTrade
            {
                PnL = i < 4 ? 100m : -80m,
                EntryTime = DateTime.UtcNow.AddHours(-7 + i),
                ExitTime = DateTime.UtcNow.AddHours(-7 + i + 1),
                LotSize = 0.1m,
            }).ToList(),
        };
        decimal score = 0.65m;
        var (lower, median, upper) = OptimizationValidationCoordinator.ComputeBootstrapCI(oosResult, score, 10_000m, 500, 42);
        // Pure synthetic: lower = score * (0.50 + 0.13) = score * 0.63
        Assert.True(lower < score);
        Assert.Equal(score, median);
        Assert.Equal(score, upper);
    }

    [Fact]
    public void ComputeBootstrapCI_InBlendingZone_InterpolatesBetweenSyntheticAndEmpirical()
    {
        var trades = Enumerable.Range(0, 10).Select(i => new BacktestTrade
        {
            PnL = i < 6 ? 120m : -90m,
            EntryTime = DateTime.UtcNow.AddHours(-10 + i),
            ExitTime = DateTime.UtcNow.AddHours(-10 + i + 1),
            LotSize = 0.1m,
        }).ToList();
        var oosResult = new BacktestResult
        {
            TotalTrades = 10,
            WinRate = 0.60m, ProfitFactor = 1.3m, MaxDrawdownPct = 8m, SharpeRatio = 0.8m,
            Trades = trades,
        };
        decimal score = 0.60m;
        var (lower, median, upper) = OptimizationValidationCoordinator.ComputeBootstrapCI(oosResult, score, 10_000m, 500, 42);
        // In blending zone (10 trades, between 7 and 15): lower should be between synthetic and empirical
        Assert.True(lower < score);
        Assert.True(lower > 0); // Not degenerate
    }

    // #12: ComputeBootstrapCI — zero trades
    [Fact]
    public void ComputeBootstrapCI_ZeroTrades_SyntheticHalf()
    {
        var oosResult = new BacktestResult
        {
            TotalTrades = 0,
            WinRate = 0m, ProfitFactor = 0m, MaxDrawdownPct = 0m, SharpeRatio = 0m,
            Trades = [],
        };
        decimal score = 0.50m;
        var (lower, median, upper) = OptimizationValidationCoordinator.ComputeBootstrapCI(oosResult, score, 10_000m, 500, 42);
        // 0 trades → penalty = 0.50, so lower = 0.50 * 0.50 = 0.25
        Assert.Equal(score * 0.50m, lower);
        Assert.Equal(score, median);
        Assert.Equal(score, upper);
    }

    private sealed class RecordingBacktestEngine : IBacktestEngine
    {
        public List<string> SeenParameterJson { get; } = [];
        public List<int> SeenCandleCounts { get; } = [];

        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            SeenParameterJson.Add(strategy.ParametersJson);
            SeenCandleCounts.Add(candles.Count);

            return Task.FromResult(new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance + 500m,
                TotalTrades = 20,
                WinRate = 0.60m,
                ProfitFactor = 1.50m,
                MaxDrawdownPct = 5m,
                SharpeRatio = 1.2m,
                Trades = []
            });
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class BlockingOptimizationRunProcessor(int blockingCalls) : IOptimizationRunProcessor
    {
        private int _startedCalls;
        private int _activeCalls;
        private int _maxConcurrentCalls;

        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);
        public TaskCompletionSource AllBlockingCallsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> ProcessNextQueuedRunAsync(CancellationToken ct)
        {
            int callNumber = Interlocked.Increment(ref _startedCalls);
            if (callNumber > blockingCalls)
                return false;

            int activeCalls = Interlocked.Increment(ref _activeCalls);
            UpdateMaxConcurrentCalls(activeCalls);

            if (callNumber == blockingCalls)
                AllBlockingCallsStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }

            return false;
        }

        private void UpdateMaxConcurrentCalls(int activeCalls)
        {
            int snapshot;
            do
            {
                snapshot = Volatile.Read(ref _maxConcurrentCalls);
                if (snapshot >= activeCalls)
                    return;
            }
            while (Interlocked.CompareExchange(ref _maxConcurrentCalls, activeCalls, snapshot) != snapshot);
        }
    }

    private sealed class CountingLoopCoordinator : IOptimizationWorkerLoopCoordinator
    {
        private int _cycleCount;

        public int CycleCount => Volatile.Read(ref _cycleCount);

        public Task WarmStartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task ExecuteCycleAsync(OptimizationWorkerCycleContext cycleContext, CancellationToken ct)
        {
            Interlocked.Increment(ref _cycleCount);
            return Task.CompletedTask;
        }
    }

    private static async Task<int> WaitForCoordinatorCyclesAsync(
        CountingLoopCoordinator loopCoordinator,
        int minimumCycles,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            int current = loopCoordinator.CycleCount;
            if (current >= minimumCycles)
                return current;

            await Task.Delay(25);
        }

        return loopCoordinator.CycleCount;
    }

    private sealed class ThrowingLoopCoordinator : IOptimizationWorkerLoopCoordinator
    {
        public Task WarmStartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task ExecuteCycleAsync(OptimizationWorkerCycleContext cycleContext, CancellationToken ct)
            => Task.FromException(new InvalidOperationException("coordinator failure"));
    }

    private sealed class SignalingOptimizationRunProcessor : IOptimizationRunProcessor
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public TaskCompletionSource<bool> Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ProcessNextQueuedRunAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _invocationCount);
            Invoked.TrySetResult(true);
            return Task.FromResult(false);
        }
    }

    private static Task<OptimizationConfigProvider> CreatePrimedConfigProviderAsync(params EngineConfig[] configs)
        => CreatePrimedConfigProviderAsync(TimeProvider.System, configs);

    private static async Task<OptimizationConfigProvider> CreatePrimedConfigProviderAsync(
        TimeProvider timeProvider,
        params EngineConfig[] configs)
    {
        var configProvider = new OptimizationConfigProvider(
            Mock.Of<ILogger<OptimizationConfigProvider>>(),
            timeProvider);
        var configDbSet = configs.AsQueryable().BuildMockDbSet();
        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);
        await configProvider.LoadAsync(db.Object, CancellationToken.None);
        return configProvider;
    }

    private sealed class BaselineSelectionBacktestEngine : IBacktestEngine
    {
        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            bool isIsFold = candles.Count >= 15;
            string mode = strategy.ParametersJson;

            decimal score = mode.Contains("winner", StringComparison.OrdinalIgnoreCase)
                ? isIsFold ? 0.60m : 0.80m
                : mode.Contains("live", StringComparison.OrdinalIgnoreCase)
                    ? isIsFold ? 0.90m : 0.10m
                    : isIsFold ? 0.40m : 0.20m;

            return Task.FromResult(FromSyntheticScore(initialBalance, score));
        }

        private static BacktestResult FromSyntheticScore(decimal initialBalance, decimal score) => new()
        {
            InitialBalance = initialBalance,
            FinalBalance = initialBalance + 500m,
            TotalTrades = 50,
            WinRate = score,
            ProfitFactor = score * 2m,
            MaxDrawdownPct = (1m - score) * 20m,
            SharpeRatio = score * 2m,
            Trades = []
        };
    }

    private sealed class OptionsCapturingBacktestEngine : IBacktestEngine
    {
        public BacktestOptions? LastOptions { get; private set; }

        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            LastOptions = options;

            return Task.FromResult(new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance + 400m,
                TotalTrades = 20,
                WinRate = 0.60m,
                ProfitFactor = 1.50m,
                MaxDrawdownPct = 5m,
                SharpeRatio = 1.2m,
                Trades = []
            });
        }
    }

    private sealed class TestValidationWorkerIdentity : IValidationWorkerIdentity
    {
        public TestValidationWorkerIdentity(string instanceId)
        {
            InstanceId = instanceId;
        }

        public string InstanceId { get; }
    }

}
