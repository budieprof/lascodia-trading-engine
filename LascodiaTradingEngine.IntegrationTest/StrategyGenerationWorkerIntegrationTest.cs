using System.Diagnostics.Metrics;
using System.Text.Json;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Strategies.Queries.DTOs;
using LascodiaTradingEngine.Application.Strategies.Queries.GetStrategy;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.IntegrationTest;

public class StrategyGenerationWorkerIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public StrategyGenerationWorkerIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private WriteApplicationDbContext CreateWriteContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private ReadApplicationDbContext CreateReadContext()
    {
        var options = new DbContextOptionsBuilder<ReadApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new ReadApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task EnsureMigratedAsync()
    {
        await using var context = CreateWriteContext();
        await context.Database.MigrateAsync();
    }

    [Fact]
    public async Task RunGenerationCycleAsync_PersistsPrimaryAndReserveCandidates_WithEventsAuditAndReadModelMetadata()
    {
        await EnsureMigratedAsync();

        string symbol = "EURUSD";
        await SeedSuccessScenarioAsync(symbol);

        await using var harness = CreateHarness(new DeterministicBacktestEngine(), new HarnessOptions
        {
            LivePerformanceBenchmark = new FixedLivePerformanceBenchmark(),
        });
        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var readCtx = CreateReadContext();
        var strategies = await readCtx.Set<Strategy>()
            .Where(s => !s.IsDeleted && s.Symbol == symbol && s.Name.StartsWith("Auto"))
            .OrderBy(s => s.Name)
            .ToListAsync();

        Assert.Equal(2, strategies.Count);

        var primary = Assert.Single(strategies, s => s.Name.StartsWith("Auto-") && !s.Name.StartsWith("Auto-Reserve"));
        var reserve = Assert.Single(strategies, s => s.Name.StartsWith("Auto-Reserve"));

        var primaryMetrics = ScreeningMetrics.FromJson(primary.ScreeningMetricsJson);
        var reserveMetrics = ScreeningMetrics.FromJson(reserve.ScreeningMetricsJson);

        Assert.NotNull(primaryMetrics);
        Assert.NotNull(reserveMetrics);

        Assert.Equal("Primary", primaryMetrics!.GenerationSource);
        Assert.Equal(MarketRegimeEnum.Trending.ToString(), primaryMetrics.ObservedRegime);
        Assert.Null(primaryMetrics.ReserveTargetRegime);
        Assert.True(primaryMetrics.LiveHaircutApplied);
        Assert.Equal(0.92, primaryMetrics.WinRateHaircutApplied, 3);
        Assert.True(primaryMetrics.IsAutoPromoted);
        Assert.True(primaryMetrics.ShufflePValue >= 0.0);

        Assert.Equal("Reserve", reserveMetrics!.GenerationSource);
        Assert.Equal(MarketRegimeEnum.Trending.ToString(), reserveMetrics.ObservedRegime);
        Assert.Equal(MarketRegimeEnum.Ranging.ToString(), reserveMetrics.ReserveTargetRegime);
        Assert.True(reserveMetrics.LiveHaircutApplied);
        Assert.True(reserveMetrics.IsAutoPromoted);
        Assert.True(reserveMetrics.ShufflePValue >= 0.0);

        var queuedBacktests = await readCtx.Set<BacktestRun>()
            .Where(r => !r.IsDeleted && (r.StrategyId == primary.Id || r.StrategyId == reserve.Id))
            .OrderBy(r => r.StrategyId)
            .ToListAsync();
        Assert.Equal(2, queuedBacktests.Count);
        Assert.All(queuedBacktests, run => Assert.Equal(RunStatus.Queued, run.Status));

        var candidateEvents = harness.EventService.PublishedEvents
            .OfType<StrategyCandidateCreatedIntegrationEvent>()
            .OrderBy(e => e.Name)
            .ToList();
        Assert.Equal(2, candidateEvents.Count);

        var reserveCreatedEvent = Assert.Single(candidateEvents, e => e.GenerationSource == "Reserve");
        Assert.Equal(MarketRegimeEnum.Trending, reserveCreatedEvent.ObservedRegime);
        Assert.Equal(MarketRegimeEnum.Ranging, reserveCreatedEvent.ReserveTargetRegime);

        var autoPromotedEvents = harness.EventService.PublishedEvents
            .OfType<StrategyAutoPromotedIntegrationEvent>()
            .OrderBy(e => e.Name)
            .ToList();
        Assert.Equal(2, autoPromotedEvents.Count);

        var reservePromotedEvent = Assert.Single(autoPromotedEvents, e => e.GenerationSource == "Reserve");
        Assert.Equal(MarketRegimeEnum.Trending, reservePromotedEvent.ObservedRegime);
        Assert.Equal(MarketRegimeEnum.Ranging, reservePromotedEvent.ReserveTargetRegime);
        Assert.True(reservePromotedEvent.LiveHaircutApplied);
        Assert.Equal(reserveMetrics.ShufflePValue, reservePromotedEvent.ShufflePValue, 6);

        var decisionLogs = await readCtx.Set<DecisionLog>()
            .Where(d => d.DecisionType == "StrategyGeneration" && d.EntityId == reserve.Id)
            .OrderBy(d => d.Id)
            .ToListAsync();
        Assert.Contains(decisionLogs, d => d.Outcome == "Created");

        var mapper = BuildMapper();
        await using var queryReadCtx = CreateReadContext();
        var handler = new GetStrategyQueryHandler(queryReadCtx, mapper);
        var dtoResponse = await handler.Handle(new GetStrategyQuery { Id = reserve.Id }, CancellationToken.None);

        Assert.True(dtoResponse.status);
        Assert.NotNull(dtoResponse.data);
        Assert.NotNull(dtoResponse.data!.ScreeningMetadata);
        Assert.Equal("Reserve", dtoResponse.data.ScreeningMetadata!.GenerationSource);
        Assert.Equal(MarketRegimeEnum.Trending.ToString(), dtoResponse.data.ScreeningMetadata.ObservedRegime);
        Assert.Equal(MarketRegimeEnum.Ranging.ToString(), dtoResponse.data.ScreeningMetadata.ReserveTargetRegime);
        Assert.True(dtoResponse.data.ScreeningMetadata.LiveHaircutApplied);
        Assert.True(dtoResponse.data.ScreeningMetadata.IsAutoPromoted);

        var cycleSummary = Assert.Single(harness.EventService.PublishedEvents.OfType<StrategyGenerationCycleCompletedIntegrationEvent>());
        Assert.Equal(2, cycleSummary.CandidatesCreated);
        Assert.Equal(1, cycleSummary.ReserveCandidatesCreated);
        Assert.True(cycleSummary.CandidatesScreened >= 2);
        Assert.True(cycleSummary.SymbolsProcessed >= 1);
        Assert.Equal(0, cycleSummary.StrategiesPruned);

        Assert.Empty(await readCtx.Set<StrategyGenerationPendingArtifact>().ToListAsync());
    }

    [Fact]
    public async Task RunGenerationCycleAsync_ReplaysDeferredArtifacts_AndClearsPendingArtifactState()
    {
        await EnsureMigratedAsync();

        var replayStrategyId = await SeedDeferredArtifactReplayScenarioAsync();

        await using var harness = CreateHarness(new DeterministicBacktestEngine());
        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var readCtx = CreateReadContext();
        Assert.Empty(await readCtx.Set<StrategyGenerationPendingArtifact>().ToListAsync());

        var decisionLogs = await readCtx.Set<DecisionLog>()
            .Where(d => d.EntityId == replayStrategyId && d.DecisionType == "StrategyGeneration")
            .OrderBy(d => d.Id)
            .ToListAsync();

        Assert.Contains(decisionLogs, d => d.Outcome == "Created");

        Assert.Contains(
            harness.EventService.PublishedEvents.OfType<StrategyCandidateCreatedIntegrationEvent>(),
            e => e.StrategyId == replayStrategyId);

        Assert.Contains(
            harness.EventService.PublishedEvents.OfType<StrategyAutoPromotedIntegrationEvent>(),
            e => e.StrategyId == replayStrategyId);
    }

    [Fact]
    public async Task RunGenerationCycleAsync_ReconcilesPendingArtifactEvents_AfterOutboxRetryPublishesThem()
    {
        await EnsureMigratedAsync();

        await SeedSuccessScenarioAsync("RETRY1");

        bool failInitialEventPublish = true;
        await using var harness = CreateHarness(new DeterministicBacktestEngine(), new HarnessOptions
        {
            LivePerformanceBenchmark = new FixedLivePerformanceBenchmark(),
            ShouldFailInitialPublish = evt =>
                failInitialEventPublish
                && (evt is StrategyCandidateCreatedIntegrationEvent or StrategyAutoPromotedIntegrationEvent),
        });

        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using (var readCtx = CreateReadContext())
        {
            var pendingArtifacts = await readCtx.Set<StrategyGenerationPendingArtifact>()
                .Where(a => !a.IsDeleted && a.QuarantinedAtUtc == null)
                .OrderBy(a => a.Id)
                .ToListAsync();
            Assert.NotEmpty(pendingArtifacts);
            Assert.All(pendingArtifacts, artifact => Assert.False(artifact.NeedsCreatedEvent));
            Assert.All(pendingArtifacts, artifact => Assert.NotNull(artifact.CandidateCreatedEventId));
            Assert.All(pendingArtifacts, artifact => Assert.Null(artifact.CandidateCreatedEventDispatchedAtUtc));
        }

        failInitialEventPublish = false;
        harness.EventService.PromoteFailedEventsToPublished(
            typeof(StrategyCandidateCreatedIntegrationEvent),
            typeof(StrategyAutoPromotedIntegrationEvent));

        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var reconciledReadCtx = CreateReadContext();
        Assert.Empty(await reconciledReadCtx.Set<StrategyGenerationPendingArtifact>()
            .Where(a => !a.IsDeleted && a.QuarantinedAtUtc == null)
            .ToListAsync());
    }

    [Fact]
    public async Task RunGenerationCycleAsync_ReconcilesCycleSummaryPublication_AfterOutboxRetryPublishesIt()
    {
        await EnsureMigratedAsync();

        await SeedFailureScenarioAsync("SUMRY1");

        bool failInitialSummaryPublish = true;
        await using var harness = CreateHarness(new DeterministicBacktestEngine(), new HarnessOptions
        {
            ShouldFailInitialPublish = evt =>
                failInitialSummaryPublish && evt is StrategyGenerationCycleCompletedIntegrationEvent,
        });

        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        string failedCycleId;
        await using (var readCtx = CreateReadContext())
        {
            var failedSummaryCycle = await readCtx.Set<StrategyGenerationCycleRun>()
                .Where(c => !c.IsDeleted && c.Status == "Completed" && c.SummaryEventId != null)
                .OrderByDescending(c => c.CompletedAtUtc)
                .FirstAsync();
            failedCycleId = failedSummaryCycle.CycleId;
            Assert.Null(failedSummaryCycle.SummaryEventDispatchedAtUtc);
            Assert.NotNull(failedSummaryCycle.SummaryEventFailedAtUtc);
        }

        failInitialSummaryPublish = false;
        harness.EventService.PromoteFailedEventsToPublished(typeof(StrategyGenerationCycleCompletedIntegrationEvent));

        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var reconciledReadCtx = CreateReadContext();
        var reconciledCycle = await reconciledReadCtx.Set<StrategyGenerationCycleRun>()
            .AsNoTracking()
            .SingleAsync(c => c.CycleId == failedCycleId);
        Assert.NotNull(reconciledCycle.SummaryEventDispatchedAtUtc);
        Assert.Null(reconciledCycle.SummaryEventFailedAtUtc);
    }

    [Fact]
    public async Task RunGenerationCycleAsync_QuarantinesCorruptPendingArtifacts_InsteadOfDroppingThem()
    {
        await EnsureMigratedAsync();

        await using var context = CreateWriteContext();
        await ResetWorkerStateAsync(context);
        await UpsertConfigAsync(context, BuildStrategyGenerationConfigs(
            ("StrategyGeneration:MaxCandidatesPerCycle", "0"),
            ("StrategyGeneration:StrategicReserveQuota", "0"),
            ("StrategyGeneration:MaxCandidatesPerWeek", "1000"),
            ("StrategyGeneration:SuppressDuringDrawdownRecovery", "false"),
            ("StrategyGeneration:SeasonalBlackoutEnabled", "false"),
            ("StrategyGeneration:SkipWeekends", "false"),
            ("StrategyGeneration:AdaptiveThresholdsEnabled", "false"),
            ("StrategyGeneration:PortfolioBacktestEnabled", "false"),
            ("StrategyGeneration:MonteCarloEnabled", "false"),
            ("StrategyGeneration:MonteCarloShuffleEnabled", "false"),
            ("FastTrack:Enabled", "false")));

        var corruptStrategy = new Strategy
        {
            Name = "Auto-Corrupt-Artifact-H1",
            Description = "Corrupt artifact seed",
            StrategyType = StrategyType.MovingAverageCrossover,
            Symbol = "BADART",
            Timeframe = Timeframe.H1,
            ParametersJson = "{\"Template\":\"Corrupt\"}",
            Status = StrategyStatus.Paused,
            LifecycleStage = StrategyLifecycleStage.Draft,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            ScreeningMetricsJson = new ScreeningMetrics
            {
                Regime = MarketRegimeEnum.Trending.ToString(),
                ObservedRegime = MarketRegimeEnum.Trending.ToString(),
                GenerationSource = "Primary",
            }.ToJson(),
        };
        context.Set<Strategy>().Add(corruptStrategy);
        await context.SaveChangesAsync();

        context.Set<StrategyGenerationPendingArtifact>().Add(new StrategyGenerationPendingArtifact
        {
            StrategyId = corruptStrategy.Id,
            CandidateId = "corrupt-artifact",
            CandidatePayloadJson = "null",
            NeedsCreationAudit = true,
            NeedsCreatedEvent = true,
            NeedsAutoPromoteEvent = false,
        });
        await context.SaveChangesAsync();

        await using var harness = CreateHarness(new DeterministicBacktestEngine());
        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var readCtx = CreateReadContext();
        var quarantined = await readCtx.Set<StrategyGenerationPendingArtifact>()
            .IgnoreQueryFilters()
            .SingleAsync(a => a.CandidateId == "corrupt-artifact");
        Assert.NotNull(quarantined.QuarantinedAtUtc);
        Assert.NotNull(quarantined.TerminalFailureReason);
        Assert.Contains("deserialized to null", quarantined.TerminalFailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunGenerationCycleAsync_WhenWorkersShareLock_OnlyOneCycleGeneratesCandidates()
    {
        await EnsureMigratedAsync();

        await SeedSuccessScenarioAsync("LOCK1");
        var sharedLock = new NonWaitingSingleLeaseDistributedLock();

        await using var harness1 = CreateHarness(
            new SlowDeterministicBacktestEngine(TimeSpan.FromMilliseconds(150)),
            new HarnessOptions
            {
                LivePerformanceBenchmark = new FixedLivePerformanceBenchmark(),
                DistributedLock = sharedLock,
            });
        await using var harness2 = CreateHarness(
            new SlowDeterministicBacktestEngine(TimeSpan.FromMilliseconds(150)),
            new HarnessOptions
            {
                LivePerformanceBenchmark = new FixedLivePerformanceBenchmark(),
                DistributedLock = sharedLock,
            });

        await Task.WhenAll(
            harness1.Worker.RunGenerationCycleAsync(CancellationToken.None),
            harness2.Worker.RunGenerationCycleAsync(CancellationToken.None));

        await using var readCtx = CreateReadContext();
        var strategies = await readCtx.Set<Strategy>()
            .Where(s => !s.IsDeleted && s.Symbol == "LOCK1" && s.Name.StartsWith("Auto"))
            .OrderBy(s => s.Id)
            .ToListAsync();
        Assert.Equal(2, strategies.Count);
    }

    [Fact]
    public async Task RunGenerationCycleAsync_PersistsStructuredFailureAudit_ForRejectedCandidate()
    {
        await EnsureMigratedAsync();

        string symbol = "GBPUSD";
        await SeedFailureScenarioAsync(symbol);

        await using var harness = CreateHarness(new DeterministicBacktestEngine());
        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var readCtx = CreateReadContext();
        var persistedStrategies = await readCtx.Set<Strategy>()
            .Where(s => !s.IsDeleted && s.Symbol == symbol && s.Name.StartsWith("Auto"))
            .ToListAsync();
        Assert.Empty(persistedStrategies);

        var failureLog = await readCtx.Set<DecisionLog>()
            .Where(d => d.DecisionType == "StrategyGeneration" && d.Outcome == "ZeroTradesIS")
            .OrderByDescending(d => d.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(failureLog);
        Assert.Contains(symbol, failureLog!.ContextJson);
        Assert.Contains("\"generationSource\":\"Primary\"", failureLog.ContextJson);
        Assert.Contains("\"failureReason\":\"ZeroTradesIS\"", failureLog.ContextJson);

        Assert.Empty(harness.EventService.PublishedEvents.OfType<StrategyCandidateCreatedIntegrationEvent>());
        Assert.Empty(harness.EventService.PublishedEvents.OfType<StrategyAutoPromotedIntegrationEvent>());
    }

    [Fact]
    public async Task RunGenerationCycleAsync_ResumesFromCheckpoint_AndPersistsPendingCandidate()
    {
        await EnsureMigratedAsync();

        await SeedCheckpointResumeScenarioAsync();

        await using var harness = CreateHarness(new DeterministicBacktestEngine(), new HarnessOptions
        {
            LivePerformanceBenchmark = new NeutralLivePerformanceBenchmark(),
        });
        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var readCtx = CreateReadContext();
        var persisted = await readCtx.Set<Strategy>()
            .Where(s => !s.IsDeleted && s.Symbol == "RESUME" && s.Name.StartsWith("Auto"))
            .SingleAsync();

        var persistedMetrics = ScreeningMetrics.FromJson(persisted.ScreeningMetricsJson);
        Assert.NotNull(persistedMetrics);
        Assert.Equal("Primary", persistedMetrics!.GenerationSource);
        Assert.Equal(MarketRegimeEnum.Trending.ToString(), persistedMetrics.ObservedRegime);

        Assert.Contains(
            harness.EventService.PublishedEvents.OfType<StrategyCandidateCreatedIntegrationEvent>(),
            evt => evt.StrategyId == persisted.Id);

        Assert.Empty(await readCtx.Set<StrategyGenerationCheckpoint>().ToListAsync());
    }

    [Fact]
    public async Task RunGenerationCycleAsync_FallsBackToIndividualPersistence_AndRecordsStructuredFailures()
    {
        await EnsureMigratedAsync();

        await SeedSuccessScenarioAsync("EURUSD");

        var failurePlan = new WriteFailurePlan
        {
            FailAfterSavingBatchBacktestRuns = true,
            FailAfterSavingReserveStrategy = true,
        };

        await using var harness = CreateHarness(new DeterministicBacktestEngine(), new HarnessOptions
        {
            WriteContextFactory = inner => new FaultInjectingWriteContext(inner, failurePlan),
            LivePerformanceBenchmark = new FixedLivePerformanceBenchmark(),
        });
        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var readCtx = CreateReadContext();
        var activeStrategies = await readCtx.Set<Strategy>()
            .Where(s => !s.IsDeleted && s.Symbol == "EURUSD" && s.Name.StartsWith("Auto"))
            .OrderBy(s => s.Id)
            .ToListAsync();
        Assert.DoesNotContain(activeStrategies, s => s.Name.StartsWith("Auto-Reserve"));

        var compensatedStrategies = await readCtx.Set<Strategy>()
            .IgnoreQueryFilters()
            .Where(s => s.Symbol == "EURUSD" && s.Name.StartsWith("Auto") && s.IsDeleted)
            .ToListAsync();
        Assert.DoesNotContain(
            compensatedStrategies,
            s => !s.Name.StartsWith("Auto-Reserve", StringComparison.Ordinal));

        var failures = await readCtx.Set<StrategyGenerationFailure>()
            .AsNoTracking()
            .Where(f => f.Symbol == "EURUSD" && f.ResolvedAtUtc == null)
            .ToListAsync();
        Assert.NotEmpty(failures);
        Assert.All(
            harness.EventService.PublishedEvents.OfType<StrategyCandidateCreatedIntegrationEvent>(),
            evt => Assert.NotEqual("Reserve", evt.GenerationSource));
        Assert.All(
            harness.EventService.PublishedEvents.OfType<StrategyAutoPromotedIntegrationEvent>(),
            evt => Assert.NotEqual("Reserve", evt.GenerationSource));
    }

    [Fact]
    public async Task RunGenerationCycleAsync_PrunesStaleDrafts_AndPublishesCycleSummary()
    {
        await EnsureMigratedAsync();

        await SeedFailureScenarioAsync("GBPUSD");
        await SeedPrunableDraftAsync("GBPUSD");

        await using var harness = CreateHarness(new DeterministicBacktestEngine(), new HarnessOptions
        {
            LivePerformanceBenchmark = new NeutralLivePerformanceBenchmark(),
        });
        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var readCtx = CreateReadContext();
        var pruned = await readCtx.Set<Strategy>()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.Name == "Auto-PruneCandidate-GBPUSD-H1");
        Assert.True(pruned.IsDeleted);
        Assert.NotNull(pruned.PrunedAtUtc);
        Assert.NotNull(ScreeningMetrics.FromJson(pruned.ScreeningMetricsJson)?.PrunedAtUtc);

        var pruneAudit = await readCtx.Set<DecisionLog>()
            .Where(d => d.EntityId == pruned.Id && d.Outcome == "Pruned")
            .SingleAsync();
        Assert.Contains("failed 3 backtests", pruneAudit.Reason);

        var cycleSummary = Assert.Single(harness.EventService.PublishedEvents.OfType<StrategyGenerationCycleCompletedIntegrationEvent>());
        Assert.Equal(1, cycleSummary.StrategiesPruned);
        Assert.Equal(0, cycleSummary.CandidatesCreated);
    }

    [Fact]
    public async Task RunGenerationCycleAsync_UsesBootstrappedHaircuts_WhenLiveHaircutsAreNeutral()
    {
        await EnsureMigratedAsync();

        await SeedSuccessScenarioAsync("BOOT1");

        await using var harness = CreateHarness(new DeterministicBacktestEngine(), new HarnessOptions
        {
            LivePerformanceBenchmark = new BootstrappedOnlyLivePerformanceBenchmark(),
        });
        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var readCtx = CreateReadContext();
        var created = await readCtx.Set<Strategy>()
            .Where(s => !s.IsDeleted
                     && s.Symbol == "BOOT1"
                     && s.Name.StartsWith("Auto")
                     && !s.Name.StartsWith("Auto-Reserve"))
            .SingleAsync();

        var metrics = ScreeningMetrics.FromJson(created.ScreeningMetricsJson);
        Assert.NotNull(metrics);
        Assert.True(metrics!.LiveHaircutApplied);
        Assert.Equal(0.83, metrics.WinRateHaircutApplied, 3);
        Assert.Equal(0.79, metrics.ProfitFactorHaircutApplied, 3);
        Assert.Equal(0.81, metrics.SharpeHaircutApplied, 3);
        Assert.Equal(1.22, metrics.DrawdownInflationApplied, 3);
    }

    [Fact]
    public async Task RunGenerationCycleAsync_PortfolioFilterRemovesCorrelatedCandidates()
    {
        await EnsureMigratedAsync();

        await SeedPortfolioFilterScenarioAsync();

        await using var harness = CreateHarness(new CorrelatedDrawdownBacktestEngine(), new HarnessOptions
        {
            LivePerformanceBenchmark = new NeutralLivePerformanceBenchmark(),
        });
        await harness.Worker.RunGenerationCycleAsync(CancellationToken.None);

        await using var readCtx = CreateReadContext();
        var created = await readCtx.Set<Strategy>()
            .Where(s => !s.IsDeleted && (s.Symbol == "PORT1" || s.Symbol == "PORT2") && s.Name.StartsWith("Auto"))
            .ToListAsync();
        Assert.True(created.Count <= 1);

        var cycleSummary = Assert.Single(harness.EventService.PublishedEvents.OfType<StrategyGenerationCycleCompletedIntegrationEvent>());
        Assert.True(cycleSummary.CandidatesCreated <= 1);
    }

    private async Task SeedSuccessScenarioAsync(string symbol)
    {
        await using var context = CreateWriteContext();
        await ResetWorkerStateAsync(context);

        await UpsertConfigAsync(context, BuildStrategyGenerationConfigs(
            ("StrategyGeneration:MaxCandidatesPerCycle", "3"),
            ("StrategyGeneration:StrategicReserveQuota", "1"),
            ("StrategyGeneration:MaxCandidatesPerWeek", "1000"),
            ("StrategyGeneration:SuppressDuringDrawdownRecovery", "false"),
            ("StrategyGeneration:SeasonalBlackoutEnabled", "false"),
            ("StrategyGeneration:SkipWeekends", "false"),
            ("StrategyGeneration:AdaptiveThresholdsEnabled", "false"),
            ("StrategyGeneration:PortfolioBacktestEnabled", "false"),
            ("StrategyGeneration:MonteCarloEnabled", "false"),
            ("StrategyGeneration:MonteCarloShuffleEnabled", "true"),
            ("StrategyGeneration:MonteCarloShufflePermutations", "32"),
            ("StrategyGeneration:MonteCarloShuffleMinPValue", "1.0"),
            ("StrategyGeneration:CandidateTimeframes", "H1"),
            ("StrategyGeneration:MaxTemplatesPerCombo", "1"),
            ("StrategyGeneration:MaxActiveStrategiesPerSymbol", "5"),
            ("StrategyGeneration:MaxActivePerTypePerSymbol", "5"),
            ("StrategyGeneration:MaxCandidatesPerCurrencyGroup", "20"),
            ("StrategyGeneration:MaxCorrelatedCandidates", "20"),
            ("StrategyGeneration:ScreeningSpreadPoints", "10"),
            ("StrategyGeneration:MinEquityCurveR2", "0.80"),
            ("StrategyGeneration:WalkForwardMinWindowsPass", "2"),
            ("FastTrack:Enabled", "true"),
            ("FastTrack:ThresholdMultiplier", "1.5"),
            ("FastTrack:MinR2", "0.80"),
            ("FastTrack:MaxMonteCarloPValue", "0.10"),
            ("FastTrack:PriorityBoost", "500")));

        if (!await context.Set<CurrencyPair>().AnyAsync(p => p.Symbol == symbol))
        {
            context.Set<CurrencyPair>().Add(new CurrencyPair
            {
                Symbol = symbol,
                BaseCurrency = "EUR",
                QuoteCurrency = "USD",
                DecimalPlaces = 5,
                ContractSize = 100_000m,
                PipSize = 10m,
                MinLotSize = 0.01m,
                MaxLotSize = 100m,
                LotStep = 0.01m,
                IsActive = true,
            });
        }

        context.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Symbol = symbol,
            Timeframe = Timeframe.H1,
            Regime = MarketRegimeEnum.Trending,
            Confidence = 0.92m,
            ADX = 30m,
            ATR = 0.0018m,
            BollingerBandWidth = 0.004m,
            DetectedAt = DateTime.UtcNow.AddHours(-1),
        });

        context.Set<Candle>().AddRange(GenerateCandles(symbol, Timeframe.H1, 260, DateTime.UtcNow.AddDays(-20)));
        await context.SaveChangesAsync();
    }

    private async Task SeedFailureScenarioAsync(string symbol)
    {
        await using var context = CreateWriteContext();
        await ResetWorkerStateAsync(context);

        await UpsertConfigAsync(context, BuildStrategyGenerationConfigs(
            ("StrategyGeneration:MaxCandidatesPerCycle", "1"),
            ("StrategyGeneration:StrategicReserveQuota", "0"),
            ("StrategyGeneration:MaxCandidatesPerWeek", "1000"),
            ("StrategyGeneration:SuppressDuringDrawdownRecovery", "false"),
            ("StrategyGeneration:SeasonalBlackoutEnabled", "false"),
            ("StrategyGeneration:SkipWeekends", "false"),
            ("StrategyGeneration:AdaptiveThresholdsEnabled", "false"),
            ("StrategyGeneration:PortfolioBacktestEnabled", "false"),
            ("StrategyGeneration:MonteCarloEnabled", "false"),
            ("StrategyGeneration:MonteCarloShuffleEnabled", "false"),
            ("StrategyGeneration:CandidateTimeframes", "H1"),
            ("StrategyGeneration:MaxTemplatesPerCombo", "1"),
            ("StrategyGeneration:MaxActiveStrategiesPerSymbol", "5"),
            ("StrategyGeneration:MaxActivePerTypePerSymbol", "5"),
            ("FastTrack:Enabled", "false")));

        if (!await context.Set<CurrencyPair>().AnyAsync(p => p.Symbol == symbol))
        {
            context.Set<CurrencyPair>().Add(new CurrencyPair
            {
                Symbol = symbol,
                BaseCurrency = "GBP",
                QuoteCurrency = "USD",
                DecimalPlaces = 5,
                ContractSize = 100_000m,
                PipSize = 10m,
                MinLotSize = 0.01m,
                MaxLotSize = 100m,
                LotStep = 0.01m,
                IsActive = true,
            });
        }

        context.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Symbol = symbol,
            Timeframe = Timeframe.H1,
            Regime = MarketRegimeEnum.Trending,
            Confidence = 0.90m,
            ADX = 28m,
            ATR = 0.0014m,
            BollingerBandWidth = 0.004m,
            DetectedAt = DateTime.UtcNow.AddMinutes(-30),
        });

        context.Set<Candle>().AddRange(GenerateCandles(symbol, Timeframe.H1, 220, DateTime.UtcNow.AddDays(-18)));
        await context.SaveChangesAsync();
    }

    private async Task<long> SeedDeferredArtifactReplayScenarioAsync()
    {
        await using var context = CreateWriteContext();
        await ResetWorkerStateAsync(context);

        await UpsertConfigAsync(context, BuildStrategyGenerationConfigs(
            ("StrategyGeneration:MaxCandidatesPerCycle", "1"),
            ("StrategyGeneration:StrategicReserveQuota", "0"),
            ("StrategyGeneration:MaxCandidatesPerWeek", "1000"),
            ("StrategyGeneration:SuppressDuringDrawdownRecovery", "false"),
            ("StrategyGeneration:SeasonalBlackoutEnabled", "false"),
            ("StrategyGeneration:SkipWeekends", "false"),
            ("StrategyGeneration:AdaptiveThresholdsEnabled", "false"),
            ("StrategyGeneration:PortfolioBacktestEnabled", "false"),
            ("StrategyGeneration:MonteCarloEnabled", "false"),
            ("StrategyGeneration:MonteCarloShuffleEnabled", "false"),
            ("FastTrack:Enabled", "false")));

        var replayStrategy = new Strategy
        {
            Name = "Auto-RSIReversion-REPLAY-H1",
            Description = "Deferred artifact replay fixture",
            StrategyType = StrategyType.RSIReversion,
            Symbol = "REPLAY",
            Timeframe = Timeframe.H1,
            ParametersJson = "{\"Template\":\"Replay\"}",
            Status = StrategyStatus.Paused,
            LifecycleStage = StrategyLifecycleStage.Draft,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            ScreeningMetricsJson = new ScreeningMetrics
            {
                Regime = MarketRegimeEnum.Ranging.ToString(),
                ObservedRegime = MarketRegimeEnum.Trending.ToString(),
                GenerationSource = "Reserve",
                ReserveTargetRegime = MarketRegimeEnum.Ranging.ToString(),
                IsWinRate = 0.69,
                IsProfitFactor = 1.9,
                IsSharpeRatio = 1.7,
                OosWinRate = 0.66,
                OosProfitFactor = 1.7,
                OosSharpeRatio = 1.4,
                WalkForwardWindowsPassed = 3,
                WalkForwardWindowsMask = 0b111,
                EquityCurveR2 = 0.91,
                MonteCarloPValue = 0,
                ShufflePValue = 0.12,
                LiveHaircutApplied = true,
                WinRateHaircutApplied = 0.92,
                ProfitFactorHaircutApplied = 0.88,
                SharpeHaircutApplied = 0.90,
                DrawdownInflationApplied = 1.15,
                IsAutoPromoted = true,
            }.ToJson(),
        };

        context.Set<Strategy>().Add(replayStrategy);
        await context.SaveChangesAsync();

        var candidate = new ScreeningOutcome
        {
            Strategy = new Strategy
            {
                Name = replayStrategy.Name,
                Description = replayStrategy.Description,
                StrategyType = replayStrategy.StrategyType,
                Symbol = replayStrategy.Symbol,
                Timeframe = replayStrategy.Timeframe,
                ParametersJson = replayStrategy.ParametersJson,
                CreatedAt = replayStrategy.CreatedAt,
                ScreeningMetricsJson = replayStrategy.ScreeningMetricsJson,
            },
            TrainResult = BuildStrongResult(DateTime.UtcNow.AddDays(-5)),
            OosResult = BuildStrongResult(DateTime.UtcNow.AddDays(-2)),
            Regime = MarketRegimeEnum.Ranging,
            ObservedRegime = MarketRegimeEnum.Trending,
            GenerationSource = "Reserve",
            Metrics = ScreeningMetrics.FromJson(replayStrategy.ScreeningMetricsJson)!,
        };

        context.Set<StrategyGenerationPendingArtifact>().Add(new StrategyGenerationPendingArtifact
        {
            StrategyId = replayStrategy.Id,
            CandidateId = new StrategyCandidateSelectionPolicy().BuildIdentity(candidate).CandidateId,
            CandidatePayloadJson = JsonSerializer.Serialize(
                GenerationCheckpointStore.PendingCandidateState.FromOutcome(candidate)),
            NeedsCreationAudit = true,
            NeedsCreatedEvent = true,
            NeedsAutoPromoteEvent = true,
        });
        await context.SaveChangesAsync();

        return replayStrategy.Id;
    }

    private async Task SeedCheckpointResumeScenarioAsync()
    {
        await using var context = CreateWriteContext();
        await ResetWorkerStateAsync(context);

        await UpsertConfigAsync(context, BuildStrategyGenerationConfigs(
            ("StrategyGeneration:MaxCandidatesPerCycle", "1"),
            ("StrategyGeneration:StrategicReserveQuota", "0"),
            ("StrategyGeneration:MaxCandidatesPerWeek", "1000"),
            ("StrategyGeneration:SuppressDuringDrawdownRecovery", "false"),
            ("StrategyGeneration:SeasonalBlackoutEnabled", "false"),
            ("StrategyGeneration:SkipWeekends", "false"),
            ("StrategyGeneration:AdaptiveThresholdsEnabled", "false"),
            ("StrategyGeneration:PortfolioBacktestEnabled", "false"),
            ("StrategyGeneration:MonteCarloEnabled", "false"),
            ("StrategyGeneration:MonteCarloShuffleEnabled", "false"),
            ("FastTrack:Enabled", "false")));

        await SeedPairRegimeAndCandlesAsync(context, "RESUME", "EUR", "USD", MarketRegimeEnum.Trending, 240);

        var candidate = BuildScreeningOutcome(
            symbol: "RESUME",
            strategyType: StrategyType.MovingAverageCrossover,
            regime: MarketRegimeEnum.Trending,
            observedRegime: MarketRegimeEnum.Trending,
            generationSource: "Primary");

        var checkpoint = new GenerationCheckpointStore.State
        {
            CycleDateUtc = DateTime.UtcNow.Date,
            Fingerprint = "integration-resume",
            CompletedSymbols = ["DONE1"],
            CandidatesCreated = 1,
            ReserveCreated = 0,
            CandidatesScreened = 1,
            SymbolsProcessed = 1,
            SymbolsSkipped = 0,
            PendingCandidates = [GenerationCheckpointStore.PendingCandidateState.FromOutcome(candidate)],
            CandidatesPerCurrency = new Dictionary<string, int> { ["EUR"] = 1, ["USD"] = 1 },
            RegimeCandidatesCreated = new Dictionary<string, int> { [MarketRegimeEnum.Trending.ToString()] = 1 },
            CorrelationGroupCounts = new Dictionary<string, int>(),
        };

        context.Set<StrategyGenerationCheckpoint>().Add(new StrategyGenerationCheckpoint
        {
            WorkerName = "StrategyGenerationWorker",
            CycleId = "integration-resume-cycle",
            CycleDateUtc = checkpoint.CycleDateUtc,
            Fingerprint = checkpoint.Fingerprint,
            PayloadJson = GenerationCheckpointStore.Serialize(checkpoint),
            UsedRestartSafeFallback = false,
            LastUpdatedAtUtc = DateTime.UtcNow,
            IsDeleted = false,
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedPrunableDraftAsync(string symbol)
    {
        await using var context = CreateWriteContext();

        var prunable = new Strategy
        {
            Name = $"Auto-PruneCandidate-{symbol}-H1",
            Description = "Prune me",
            StrategyType = StrategyType.MovingAverageCrossover,
            Symbol = symbol,
            Timeframe = Timeframe.H1,
            ParametersJson = "{\"Template\":\"Prune\"}",
            Status = StrategyStatus.Paused,
            LifecycleStage = StrategyLifecycleStage.Draft,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            ScreeningMetricsJson = new ScreeningMetrics
            {
                Regime = MarketRegimeEnum.Trending.ToString(),
                ObservedRegime = MarketRegimeEnum.Trending.ToString(),
                GenerationSource = "Primary",
                IsWinRate = 0.62,
                IsProfitFactor = 1.2,
                IsSharpeRatio = 0.9,
                OosWinRate = 0.58,
                OosProfitFactor = 1.05,
                OosSharpeRatio = 0.5,
            }.ToJson(),
        };
        context.Set<Strategy>().Add(prunable);
        await context.SaveChangesAsync();

        context.Set<BacktestRun>().AddRange(
            Enumerable.Range(0, 3).Select(i => new BacktestRun
            {
                StrategyId = prunable.Id,
                Symbol = symbol,
                Timeframe = Timeframe.H1,
                FromDate = DateTime.UtcNow.AddMonths(-3),
                ToDate = DateTime.UtcNow.AddMonths(-2),
                InitialBalance = 10_000m,
                Status = RunStatus.Failed,
                CreatedAt = DateTime.UtcNow.AddDays(-(5 - i)),
                CompletedAt = DateTime.UtcNow.AddDays(-(5 - i)).AddHours(1),
                ErrorMessage = "Integration prune failure",
            }));
        await context.SaveChangesAsync();
    }

    private async Task SeedAdaptiveThresholdScenarioAsync(string symbol)
    {
        await using var context = CreateWriteContext();
        await ResetWorkerStateAsync(context);

        await UpsertConfigAsync(context, BuildStrategyGenerationConfigs(
            ("StrategyGeneration:MaxCandidatesPerCycle", "1"),
            ("StrategyGeneration:StrategicReserveQuota", "0"),
            ("StrategyGeneration:MaxCandidatesPerWeek", "1000"),
            ("StrategyGeneration:SuppressDuringDrawdownRecovery", "false"),
            ("StrategyGeneration:SeasonalBlackoutEnabled", "false"),
            ("StrategyGeneration:SkipWeekends", "false"),
            ("StrategyGeneration:AdaptiveThresholdsEnabled", "true"),
            ("StrategyGeneration:AdaptiveThresholdsMinSamples", "10"),
            ("StrategyGeneration:MinSharpeRatio", "1.90"),
            ("StrategyGeneration:PortfolioBacktestEnabled", "false"),
            ("StrategyGeneration:MonteCarloEnabled", "false"),
            ("StrategyGeneration:MonteCarloShuffleEnabled", "false"),
            ("FastTrack:Enabled", "false")));

        await SeedPairRegimeAndCandlesAsync(context, symbol, "EUR", "USD", MarketRegimeEnum.Trending, 240);

        var historicalStrategies = Enumerable.Range(0, 10).Select(i => new Strategy
        {
            Name = $"Auto-Historical-{symbol}-CTX{i}",
            Description = "Adaptive seed",
            StrategyType = StrategyType.MovingAverageCrossover,
            Symbol = $"A{i:000}",
            Timeframe = Timeframe.H1,
            ParametersJson = $"{{\"Template\":\"Hist{i}\"}}",
            Status = StrategyStatus.Paused,
            LifecycleStage = StrategyLifecycleStage.BacktestQualified,
            CreatedAt = DateTime.UtcNow.AddDays(-(20 - i)),
            ScreeningMetricsJson = new ScreeningMetrics
            {
                Regime = MarketRegimeEnum.Trending.ToString(),
                ObservedRegime = MarketRegimeEnum.Trending.ToString(),
                GenerationSource = "Primary",
                IsWinRate = 0.64,
                IsProfitFactor = 1.22,
                IsSharpeRatio = 1.0,
                OosWinRate = 0.61,
                OosProfitFactor = 1.10,
                OosSharpeRatio = 0.92,
            }.ToJson(),
        });

        context.Set<Strategy>().AddRange(historicalStrategies);
        await context.SaveChangesAsync();
    }

    private async Task SeedPortfolioFilterScenarioAsync()
    {
        await using var context = CreateWriteContext();
        await ResetWorkerStateAsync(context);

        await UpsertConfigAsync(context, BuildStrategyGenerationConfigs(
            ("StrategyGeneration:MaxCandidatesPerCycle", "2"),
            ("StrategyGeneration:StrategicReserveQuota", "0"),
            ("StrategyGeneration:MaxCandidatesPerWeek", "1000"),
            ("StrategyGeneration:SuppressDuringDrawdownRecovery", "false"),
            ("StrategyGeneration:SeasonalBlackoutEnabled", "false"),
            ("StrategyGeneration:SkipWeekends", "false"),
            ("StrategyGeneration:AdaptiveThresholdsEnabled", "false"),
            ("StrategyGeneration:PortfolioBacktestEnabled", "true"),
            ("StrategyGeneration:MaxPortfolioDrawdownPct", "0.02"),
            ("StrategyGeneration:MinWinRate", "0.40"),
            ("StrategyGeneration:MinProfitFactor", "1.05"),
            ("StrategyGeneration:MinSharpeRatio", "0.10"),
            ("StrategyGeneration:MinEquityCurveR2", "0.0"),
            ("StrategyGeneration:MaxTradeTimeConcentration", "1.0"),
            ("StrategyGeneration:MonteCarloEnabled", "false"),
            ("StrategyGeneration:MonteCarloShuffleEnabled", "false"),
            ("FastTrack:Enabled", "false")));

        await SeedPairRegimeAndCandlesAsync(context, "PORT1", "EUR", "USD", MarketRegimeEnum.Trending, 240);
        await SeedPairRegimeAndCandlesAsync(context, "PORT2", "GBP", "USD", MarketRegimeEnum.Trending, 240);
        await context.SaveChangesAsync();
    }

    private async Task SeedPairRegimeAndCandlesAsync(
        WriteApplicationDbContext context,
        string symbol,
        string baseCurrency,
        string quoteCurrency,
        MarketRegimeEnum regime,
        int candleCount)
    {
        if (!await context.Set<CurrencyPair>().AnyAsync(p => p.Symbol == symbol))
        {
            context.Set<CurrencyPair>().Add(new CurrencyPair
            {
                Symbol = symbol,
                BaseCurrency = baseCurrency,
                QuoteCurrency = quoteCurrency,
                DecimalPlaces = 5,
                ContractSize = 100_000m,
                PipSize = 10m,
                MinLotSize = 0.01m,
                MaxLotSize = 100m,
                LotStep = 0.01m,
                IsActive = true,
            });
        }

        context.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Symbol = symbol,
            Timeframe = Timeframe.H1,
            Regime = regime,
            Confidence = 0.92m,
            ADX = 30m,
            ATR = 0.0018m,
            BollingerBandWidth = 0.004m,
            DetectedAt = DateTime.UtcNow.AddHours(-1),
        });

        context.Set<Candle>().AddRange(GenerateCandles(symbol, Timeframe.H1, candleCount, DateTime.UtcNow.AddDays(-20)));
    }

    private static async Task ResetWorkerStateAsync(WriteApplicationDbContext context)
    {
        await context.Set<BacktestRun>().ExecuteDeleteAsync();
        await context.Set<DecisionLog>().ExecuteDeleteAsync();
        await context.Set<StrategyGenerationPendingArtifact>().IgnoreQueryFilters().ExecuteDeleteAsync();
        await context.Set<StrategyGenerationCheckpoint>().IgnoreQueryFilters().ExecuteDeleteAsync();
        await context.Set<StrategyGenerationFailure>().IgnoreQueryFilters().ExecuteDeleteAsync();
        await context.Set<Strategy>().IgnoreQueryFilters().ExecuteDeleteAsync();
        await context.Set<Candle>().ExecuteDeleteAsync();
        await context.Set<MarketRegimeSnapshot>().ExecuteDeleteAsync();
        await context.Set<CurrencyPair>().ExecuteDeleteAsync();
        await context.Set<DrawdownSnapshot>().ExecuteDeleteAsync();
        await context.Set<EngineConfig>().ExecuteDeleteAsync();
    }

    private static ScreeningOutcome BuildScreeningOutcome(
        string symbol,
        StrategyType strategyType,
        MarketRegimeEnum regime,
        MarketRegimeEnum observedRegime,
        string generationSource)
    {
        var metrics = new ScreeningMetrics
        {
            Regime = regime.ToString(),
            ObservedRegime = observedRegime.ToString(),
            GenerationSource = generationSource,
            IsWinRate = 0.72,
            IsProfitFactor = 2.4,
            IsSharpeRatio = 1.65,
            OosWinRate = 0.68,
            OosProfitFactor = 1.9,
            OosSharpeRatio = 1.4,
            WalkForwardWindowsPassed = 3,
            WalkForwardWindowsMask = 0b111,
            EquityCurveR2 = 0.91,
            MonteCarloPValue = 0,
            ShufflePValue = 0,
        };

        return new ScreeningOutcome
        {
            Strategy = new Strategy
            {
                Name = $"Auto-{strategyType}-{symbol}-H1",
                Description = "Checkpoint survivor",
                StrategyType = strategyType,
                Symbol = symbol,
                Timeframe = Timeframe.H1,
                ParametersJson = "{\"Template\":\"Resume\"}",
                Status = StrategyStatus.Paused,
                LifecycleStage = StrategyLifecycleStage.Draft,
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
                ScreeningMetricsJson = metrics.ToJson(),
            },
            TrainResult = BuildStrongResult(DateTime.UtcNow.AddDays(-5)),
            OosResult = BuildStrongResult(DateTime.UtcNow.AddDays(-2)),
            Regime = regime,
            ObservedRegime = observedRegime,
            GenerationSource = generationSource,
            Metrics = metrics,
        };
    }

    private WorkerHarness CreateHarness(IBacktestEngine backtestEngine, HarnessOptions? options = null)
    {
        options ??= new HarnessOptions();
        var eventService = new CapturingIntegrationEventService(options.ShouldFailInitialPublish);
        var meterFactory = new TestMeterFactory();
        var metrics = new TradingMetrics(meterFactory);
        var correlationOptions = new CorrelationGroupOptions
        {
            Groups =
            [
                ["EURUSD", "GBPUSD", "AUDUSD", "NZDUSD"],
                ["USDCHF", "USDJPY", "USDCAD"],
                ["EURJPY", "GBPJPY", "AUDJPY"],
            ],
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AutoRegisterAttributedServices(typeof(StrategyGenerationWorker).Assembly);
        services.AddSingleton(metrics);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(correlationOptions);
        services.AddSingleton<IBacktestEngine>(backtestEngine);
        services.AddSingleton<IRegimeStrategyMapper>(options.RegimeStrategyMapper ?? new FixedRegimeStrategyMapper());
        services.AddSingleton<IStrategyParameterTemplateProvider>(options.TemplateProvider ?? new FixedTemplateProvider());
        services.AddSingleton<IValidationSettingsProvider, ValidationSettingsProvider>();
        services.AddSingleton<IStrategyExecutionSnapshotBuilder, StrategyExecutionSnapshotBuilder>();
        services.AddSingleton<IBacktestOptionsSnapshotBuilder, BacktestOptionsSnapshotBuilder>();
        services.AddSingleton<IValidationRunFactory, ValidationRunFactory>();
        services.AddSingleton<ILivePriceCache, InMemoryLivePriceCache>();
        services.AddSingleton<IFeedbackDecayMonitor>(options.FeedbackDecayMonitor ?? new NoOpFeedbackDecayMonitor());
        services.AddSingleton<IDistributedLock>(options.DistributedLock ?? new TestDistributedLock());
        services.AddSingleton<IWorkerHealthMonitor, WorkerHealthMonitor>();
        services.AddScoped<IReadApplicationDbContext>(_ => CreateReadContext());
        services.AddScoped<IWriteApplicationDbContext>(_ =>
        {
            var inner = CreateWriteContext();
            return options.WriteContextFactory != null
                ? options.WriteContextFactory(inner)
                : inner;
        });
        services.AddScoped<IMediator, DecisionLogMediator>();
        services.AddSingleton(eventService);
        services.AddSingleton<IIntegrationEventService>(sp => sp.GetRequiredService<CapturingIntegrationEventService>());
        services.AddSingleton<IEventLogReader>(sp => sp.GetRequiredService<CapturingIntegrationEventService>());
        if (options.LivePerformanceBenchmark != null)
            services.AddSingleton<ILivePerformanceBenchmark>(options.LivePerformanceBenchmark);
        services.AddSingleton<StrategyGenerationWorker>();

        var provider = services.BuildServiceProvider(validateScopes: true);
        var worker = provider.GetRequiredService<StrategyGenerationWorker>();

        return new WorkerHarness(worker, provider, eventService, meterFactory);
    }

    private static async Task UpsertConfigAsync(WriteApplicationDbContext context, IEnumerable<EngineConfig> configs)
    {
        foreach (var config in configs)
        {
            var existing = await context.Set<EngineConfig>().FirstOrDefaultAsync(c => c.Key == config.Key);
            if (existing == null)
            {
                context.Set<EngineConfig>().Add(config);
                continue;
            }

            existing.Value = config.Value;
            existing.Description = config.Description;
            existing.DataType = config.DataType;
            existing.IsHotReloadable = config.IsHotReloadable;
            existing.LastUpdatedAt = DateTime.UtcNow;
            existing.IsDeleted = false;
        }
    }

    private static IReadOnlyList<EngineConfig> BuildStrategyGenerationConfigs(params (string Key, string Value)[] overrides)
    {
        var configs = new List<EngineConfig>
        {
            NewConfig("StrategyGeneration:Enabled", "true"),
            NewConfig("StrategyGeneration:ScheduleHourUtc", DateTime.UtcNow.Hour.ToString()),
            NewConfig("StrategyGeneration:ScreeningWindowMonths", "6"),
            NewConfig("StrategyGeneration:MinWinRate", "0.60"),
            NewConfig("StrategyGeneration:MinProfitFactor", "1.10"),
            NewConfig("StrategyGeneration:MinSharpeRatio", "0.30"),
            NewConfig("StrategyGeneration:MinTotalTrades", "15"),
            NewConfig("StrategyGeneration:MaxDrawdownPct", "0.20"),
            NewConfig("StrategyGeneration:MaxCandidatesPerCycle", "50"),
            NewConfig("StrategyGeneration:MaxActiveStrategiesPerSymbol", "3"),
            NewConfig("StrategyGeneration:MaxActivePerTypePerSymbol", "2"),
            NewConfig("StrategyGeneration:PruneAfterFailedBacktests", "3"),
            NewConfig("StrategyGeneration:RegimeFreshnessHours", "48"),
            NewConfig("StrategyGeneration:RetryCooldownDays", "30"),
            NewConfig("StrategyGeneration:MaxCandidatesPerCurrencyGroup", "6"),
            NewConfig("StrategyGeneration:ScreeningSpreadPoints", "20"),
            NewConfig("StrategyGeneration:ScreeningCommissionPerLot", "7.0"),
            NewConfig("StrategyGeneration:ScreeningSlippagePips", "1.0"),
            NewConfig("StrategyGeneration:MinRegimeConfidence", "0.60"),
            NewConfig("StrategyGeneration:MaxOosDegradationPct", "0.60"),
            NewConfig("StrategyGeneration:SuppressDuringDrawdownRecovery", "false"),
            NewConfig("StrategyGeneration:SeasonalBlackoutEnabled", "false"),
            NewConfig("StrategyGeneration:BlackoutPeriods", "12/20-01/05"),
            NewConfig("StrategyGeneration:ScreeningTimeoutSeconds", "30"),
            NewConfig("StrategyGeneration:CandidateTimeframes", "H1"),
            NewConfig("StrategyGeneration:MaxTemplatesPerCombo", "1"),
            NewConfig("StrategyGeneration:StrategicReserveQuota", "0"),
            NewConfig("StrategyGeneration:MaxCandidatesPerWeek", "1000"),
            NewConfig("StrategyGeneration:MaxSpreadToRangeRatio", "0.30"),
            NewConfig("StrategyGeneration:ScreeningInitialBalance", "10000"),
            NewConfig("StrategyGeneration:MaxParallelBacktests", "2"),
            NewConfig("StrategyGeneration:RegimeBudgetDiversityPct", "1.0"),
            NewConfig("StrategyGeneration:MinEquityCurveR2", "0.70"),
            NewConfig("StrategyGeneration:MaxTradeTimeConcentration", "0.60"),
            NewConfig("StrategyGeneration:CircuitBreakerMaxFailures", "3"),
            NewConfig("StrategyGeneration:CircuitBreakerBackoffDays", "2"),
            NewConfig("StrategyGeneration:ConsecutiveFailures", "0"),
            NewConfig("StrategyGeneration:RetriesThisWindow", "0"),
            NewConfig("StrategyGeneration:RetryWindowDateUtc", ""),
            NewConfig("StrategyGeneration:CircuitBreakerUntilUtc", ""),
            NewConfig("StrategyGeneration:LastRunDateUtc", ""),
            NewConfig("StrategyGeneration:MaxCandleCacheSize", "500000"),
            NewConfig("StrategyGeneration:CandleChunkSize", "20"),
            NewConfig("StrategyGeneration:MaxCorrelatedCandidates", "10"),
            NewConfig("StrategyGeneration:AdaptiveThresholdsEnabled", "false"),
            NewConfig("StrategyGeneration:AdaptiveThresholdsMinSamples", "10"),
            NewConfig("StrategyGeneration:MonteCarloEnabled", "false"),
            NewConfig("StrategyGeneration:MonteCarloPermutations", "32"),
            NewConfig("StrategyGeneration:MonteCarloMinPValue", "0.05"),
            NewConfig("StrategyGeneration:MonteCarloShuffleEnabled", "false"),
            NewConfig("StrategyGeneration:MonteCarloShufflePermutations", "32"),
            NewConfig("StrategyGeneration:MonteCarloShuffleMinPValue", "1.0"),
            NewConfig("StrategyGeneration:PortfolioBacktestEnabled", "false"),
            NewConfig("StrategyGeneration:MaxPortfolioDrawdownPct", "0.30"),
            NewConfig("StrategyGeneration:PortfolioCorrelationWeight", "0.05"),
            NewConfig("StrategyGeneration:MaxCandleAgeHours", "0"),
            NewConfig("StrategyGeneration:SkipWeekends", "false"),
            NewConfig("StrategyGeneration:BlackoutTimezone", "UTC"),
            NewConfig("StrategyGeneration:RegimeTransitionCooldownHours", "0"),
            NewConfig("StrategyGeneration:WalkForwardWindowCount", "3"),
            NewConfig("StrategyGeneration:WalkForwardMinWindowsPass", "2"),
            NewConfig("StrategyGeneration:WalkForwardSplitPcts", "0.40,0.55,0.70"),
            NewConfig("FastTrack:Enabled", "false"),
            NewConfig("FastTrack:ThresholdMultiplier", "2.0"),
            NewConfig("FastTrack:MinR2", "0.90"),
            NewConfig("FastTrack:MaxMonteCarloPValue", "0.01"),
            NewConfig("FastTrack:PriorityBoost", "1000"),
            NewConfig("StrategyGeneration:PendingPostPersistArtifacts", ""),
            NewConfig("StrategyGeneration:FailedCandidateKeys", ""),
            NewConfig("StrategyGeneration:PreviousCycleStats", ""),
            NewConfig("StrategyGeneration:FeedbackSummary", ""),
        };

        foreach (var (key, value) in overrides)
        {
            var existing = configs.FirstOrDefault(c => c.Key == key);
            if (existing == null)
            {
                configs.Add(NewConfig(key, value));
            }
            else
            {
                existing.Value = value;
            }
        }

        return configs;
    }

    private static EngineConfig NewConfig(string key, string value) => new()
    {
        Key = key,
        Value = value,
        Description = $"Integration test config for {key}",
        DataType = ConfigDataType.String,
        IsHotReloadable = true,
        LastUpdatedAt = DateTime.UtcNow,
        IsDeleted = false,
    };

    private static List<Candle> GenerateCandles(string symbol, Timeframe timeframe, int count, DateTime startUtc)
    {
        var candles = new List<Candle>(count);
        for (int i = 0; i < count; i++)
        {
            decimal basePrice = 1.0800m + i * 0.00012m;
            candles.Add(new Candle
            {
                Symbol = symbol,
                Timeframe = timeframe,
                Timestamp = startUtc.AddHours(i),
                Open = basePrice,
                High = basePrice + 0.00045m,
                Low = basePrice - 0.00025m,
                Close = basePrice + 0.00020m,
                Volume = 1000 + i,
                IsClosed = true,
            });
        }

        return candles;
    }

    private static IMapper BuildMapper()
    {
        var expression = new MapperConfigurationExpression();
        expression.AddProfile(new MappingProfile(new HttpContextAccessor(), typeof(StrategyDto).Assembly));
        var configuration = new MapperConfiguration(expression, NullLoggerFactory.Instance);
        configuration.AssertConfigurationIsValid();
        return configuration.CreateMapper();
    }

    private static BacktestResult BuildStrongResult(DateTime startUtc, double sharpe = 1.65, int tradeCount = 12)
    {
        var trades = new List<BacktestTrade>(tradeCount);
        for (int i = 0; i < tradeCount; i++)
        {
            decimal pnl = 85m + (i * 8m);
            trades.Add(new BacktestTrade
            {
                Direction = TradeDirection.Buy,
                EntryPrice = 1.1000m + i * 0.0002m,
                ExitPrice = 1.1010m + i * 0.0002m,
                LotSize = 0.10m,
                PnL = pnl,
                GrossPnL = pnl,
                Commission = 0,
                Swap = 0,
                Slippage = 0,
                EntryTime = startUtc.AddHours(i * 2),
                ExitTime = startUtc.AddHours((i * 2) + 1),
                ExitReason = TradeExitReason.TakeProfit,
            });
        }

        return new BacktestResult
        {
            InitialBalance = 10_000m,
            FinalBalance = 11_500m,
            TotalReturn = 0.15m,
            TotalTrades = tradeCount,
            WinningTrades = tradeCount,
            LosingTrades = 0,
            WinRate = 0.72m,
            ProfitFactor = 2.4m,
            MaxDrawdownPct = 0.08m,
            SharpeRatio = (decimal)sharpe,
            SortinoRatio = 2.1m,
            CalmarRatio = 1.9m,
            AverageWin = 120m,
            AverageLoss = 0m,
            LargestWin = 180m,
            LargestLoss = 0m,
            Expectancy = 120m,
            MaxConsecutiveWins = tradeCount,
            MaxConsecutiveLosses = 0,
            ExposurePct = 0.32m,
            AverageTradeDurationHours = 1.5,
            TotalCommission = 0,
            TotalSwap = 0,
            TotalSlippage = 0,
            RecoveryFactor = 2.3m,
            Trades = trades,
        };
    }

    private static readonly BacktestResult ZeroTradeResult = new()
    {
        InitialBalance = 10_000m,
        FinalBalance = 10_000m,
        TotalReturn = 0m,
        TotalTrades = 0,
        WinningTrades = 0,
        LosingTrades = 0,
        WinRate = 0m,
        ProfitFactor = 0m,
        MaxDrawdownPct = 0m,
        SharpeRatio = 0m,
        AverageWin = 0m,
        AverageLoss = 0m,
        LargestWin = 0m,
        LargestLoss = 0m,
        Expectancy = 0m,
        Trades = [],
    };

    private sealed class HarnessOptions
    {
        public Func<WriteApplicationDbContext, IWriteApplicationDbContext>? WriteContextFactory { get; init; }
        public IStrategyParameterTemplateProvider? TemplateProvider { get; init; }
        public IRegimeStrategyMapper? RegimeStrategyMapper { get; init; }
        public ILivePerformanceBenchmark? LivePerformanceBenchmark { get; init; }
        public IFeedbackDecayMonitor? FeedbackDecayMonitor { get; init; }
        public Func<IntegrationEvent, bool>? ShouldFailInitialPublish { get; init; }
        public IDistributedLock? DistributedLock { get; init; }
    }

    private sealed class WriteFailurePlan
    {
        private int _batchBacktestFailureConsumed;
        private int _reserveStrategyFailureConsumed;

        public bool FailAfterSavingBatchBacktestRuns { get; init; }
        public bool FailAfterSavingReserveStrategy { get; init; }

        public bool TryConsumeBatchBacktestFailure() =>
            FailAfterSavingBatchBacktestRuns &&
            Interlocked.CompareExchange(ref _batchBacktestFailureConsumed, 1, 0) == 0;

        public bool TryConsumeReserveStrategyFailure() =>
            FailAfterSavingReserveStrategy &&
            Interlocked.CompareExchange(ref _reserveStrategyFailureConsumed, 1, 0) == 0;
    }

    private sealed class WorkerHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly TestMeterFactory _meterFactory;

        public WorkerHarness(
            StrategyGenerationWorker worker,
            ServiceProvider provider,
            CapturingIntegrationEventService eventService,
            TestMeterFactory meterFactory)
        {
            Worker = worker;
            _provider = provider;
            EventService = eventService;
            _meterFactory = meterFactory;
        }

        public StrategyGenerationWorker Worker { get; }
        public CapturingIntegrationEventService EventService { get; }

        public ValueTask DisposeAsync()
        {
            _provider.Dispose();
            _meterFactory.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultInjectingWriteContext : IWriteApplicationDbContext
    {
        private readonly WriteApplicationDbContext _inner;
        private readonly WriteFailurePlan _plan;

        public FaultInjectingWriteContext(WriteApplicationDbContext inner, WriteFailurePlan plan)
        {
            _inner = inner;
            _plan = plan;
        }

        public DbContext GetDbContext() => _inner;

        public int SaveChanges() => _inner.SaveChanges();

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var addedStrategies = _inner.ChangeTracker.Entries<Strategy>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity)
                .ToList();
            var addedBacktestRuns = _inner.ChangeTracker.Entries<BacktestRun>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity)
                .ToList();

            bool failAfterBatchBacktestSave = addedBacktestRuns.Count >= 2 && _plan.TryConsumeBatchBacktestFailure();
            bool failAfterReserveStrategySave = addedStrategies.Count == 1
                && addedStrategies[0].Name.StartsWith("Auto-Reserve", StringComparison.OrdinalIgnoreCase)
                && _plan.TryConsumeReserveStrategyFailure();

            int result = await _inner.SaveChangesAsync(cancellationToken);

            if (failAfterBatchBacktestSave)
                throw new InvalidOperationException("Injected integration failure after batch backtest-run persistence.");

            if (failAfterReserveStrategySave)
                throw new InvalidOperationException("Injected integration failure after reserve strategy persistence.");

            return result;
        }
    }

    private sealed class DeterministicBacktestEngine : IBacktestEngine
    {
        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            ct.ThrowIfCancellationRequested();

            if (strategy.Symbol.Equals("GBPUSD", StringComparison.OrdinalIgnoreCase)
                || strategy.ParametersJson.Contains("ZeroTrades", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(ZeroTradeResult);

            double sharpe = options?.PositionSizer != null ? 1.40 : 1.65;
            return Task.FromResult(BuildStrongResult(
                candles.Count > 0 ? candles[0].Timestamp : DateTime.UtcNow.AddDays(-5),
                sharpe));
        }
    }

    private sealed class SlowDeterministicBacktestEngine : IBacktestEngine
    {
        private readonly TimeSpan _delay;
        private readonly DeterministicBacktestEngine _inner = new();

        public SlowDeterministicBacktestEngine(TimeSpan delay)
        {
            _delay = delay;
        }

        public async Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            await Task.Delay(_delay, ct);
            return await _inner.RunAsync(strategy, candles, initialBalance, ct, options);
        }
    }

    private sealed class CorrelatedDrawdownBacktestEngine : IBacktestEngine
    {
        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            ct.ThrowIfCancellationRequested();

            var trades = new List<BacktestTrade>();
            decimal[] pnls = [-420m, -360m, 520m, 280m, -140m, 460m, 220m, -90m, 260m, 180m];
            var start = candles.Count > 0 ? candles[0].Timestamp.Date : DateTime.UtcNow.Date.AddDays(-20);
            for (int i = 0; i < pnls.Length; i++)
            {
                trades.Add(new BacktestTrade
                {
                    Direction = TradeDirection.Buy,
                    EntryPrice = 1.1000m + i * 0.0001m,
                    ExitPrice = 1.1005m + i * 0.0001m,
                    LotSize = 0.10m,
                    PnL = pnls[i],
                    GrossPnL = pnls[i],
                    EntryTime = start.AddDays(i).AddHours(1),
                    ExitTime = start.AddDays(i).AddHours(2),
                    ExitReason = pnls[i] >= 0 ? TradeExitReason.TakeProfit : TradeExitReason.StopLoss,
                });
            }

            return Task.FromResult(new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance + pnls.Sum(),
                TotalReturn = 0.09m,
                TotalTrades = trades.Count,
                WinningTrades = 6,
                LosingTrades = 4,
                WinRate = 0.60m,
                ProfitFactor = 1.45m,
                MaxDrawdownPct = 0.18m,
                SharpeRatio = 1.10m,
                AverageWin = 320m,
                AverageLoss = 252m,
                LargestWin = 520m,
                LargestLoss = 420m,
                Expectancy = 90m,
                Trades = trades,
            });
        }
    }

    private sealed class FixedRegimeStrategyMapper : IRegimeStrategyMapper
    {
        public IReadOnlyList<StrategyType> GetStrategyTypes(MarketRegimeEnum regime) => regime switch
        {
            MarketRegimeEnum.Trending => [StrategyType.MovingAverageCrossover],
            MarketRegimeEnum.Ranging => [StrategyType.RSIReversion],
            _ => [StrategyType.MovingAverageCrossover],
        };

        public void RefreshFromFeedback(IReadOnlyDictionary<(StrategyType, MarketRegimeEnum), double> feedbackRates, double promotionThreshold = 0.65)
        {
        }
    }

    private sealed class FixedTemplateProvider : IStrategyParameterTemplateProvider
    {
        public IReadOnlyList<string> GetTemplates(StrategyType strategyType) => strategyType switch
        {
            StrategyType.MovingAverageCrossover => ["{\"Template\":\"Primary\"}"],
            StrategyType.RSIReversion => ["{\"Template\":\"Reserve\"}"],
            StrategyType.BollingerBandReversion => ["{\"Template\":\"ReserveAlt\"}"],
            _ => ["{\"Template\":\"Default\"}"],
        };

        public void RefreshDynamicTemplates(IReadOnlyDictionary<StrategyType, IReadOnlyList<string>> promotedParams)
        {
        }
    }

    private sealed class InMemoryLivePriceCache : ILivePriceCache
    {
        private readonly Dictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> _prices = new(StringComparer.OrdinalIgnoreCase);

        public void Update(string symbol, decimal bid, decimal ask, DateTime timestamp)
            => _prices[symbol] = (bid, ask, timestamp);

        public (decimal Bid, decimal Ask, DateTime Timestamp)? Get(string symbol)
            => _prices.TryGetValue(symbol, out var price) ? price : null;

        public IReadOnlyDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> GetAll()
            => _prices;
    }

    private sealed class FixedLivePerformanceBenchmark : ILivePerformanceBenchmark
    {
        private static readonly HaircutRatios Ratios = new(0.92, 0.88, 0.90, 1.15, 8);

        public Task<HaircutRatios> ComputeHaircutsAsync(CancellationToken ct) => Task.FromResult(Ratios);
        public Task<HaircutRatios> GetCachedHaircutsAsync(CancellationToken ct) => Task.FromResult(Ratios);
        public Task<HaircutRatios> ComputeBootstrappedHaircutsAsync(CancellationToken ct)
            => Task.FromResult(HaircutRatios.Neutral);
    }

    private sealed class NeutralLivePerformanceBenchmark : ILivePerformanceBenchmark
    {
        public Task<HaircutRatios> ComputeHaircutsAsync(CancellationToken ct) => Task.FromResult(HaircutRatios.Neutral);
        public Task<HaircutRatios> GetCachedHaircutsAsync(CancellationToken ct) => Task.FromResult(HaircutRatios.Neutral);
        public Task<HaircutRatios> ComputeBootstrappedHaircutsAsync(CancellationToken ct) => Task.FromResult(HaircutRatios.Neutral);
    }

    private sealed class BootstrappedOnlyLivePerformanceBenchmark : ILivePerformanceBenchmark
    {
        private static readonly HaircutRatios BootstrappedRatios = new(0.83, 0.79, 0.81, 1.22, -7);

        public Task<HaircutRatios> ComputeHaircutsAsync(CancellationToken ct) => Task.FromResult(HaircutRatios.Neutral);
        public Task<HaircutRatios> GetCachedHaircutsAsync(CancellationToken ct) => Task.FromResult(HaircutRatios.Neutral);
        public Task<HaircutRatios> ComputeBootstrappedHaircutsAsync(CancellationToken ct) => Task.FromResult(BootstrappedRatios);
    }

    private sealed class NoOpFeedbackDecayMonitor : IFeedbackDecayMonitor
    {
        public double GetEffectiveHalfLifeDays() => 62.0;

        public Task RecordPredictionsAsync(
            DbContext readDb,
            DbContext writeDb,
            Dictionary<(StrategyType, MarketRegimeEnum), double> predictions,
            CancellationToken ct) => Task.CompletedTask;

        public Task EvaluateAndAdjustAsync(DbContext readDb, DbContext writeDb, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class DecisionLogMediator : IMediator
    {
        private readonly LogDecisionCommandHandler _handler;

        public DecisionLogMediator(IWriteApplicationDbContext writeContext)
        {
            _handler = new LogDecisionCommandHandler(writeContext);
        }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is LogDecisionCommand command)
                return HandleCommand<TResponse>(command, cancellationToken);

            throw new NotSupportedException($"Request type {request.GetType().Name} is not supported by the integration-test mediator.");
        }

        public async Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            if (request is LogDecisionCommand command)
                return await _handler.Handle(command, cancellationToken);

            throw new NotSupportedException($"Request type {request.GetType().Name} is not supported by the integration-test mediator.");
        }

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming requests are not used in this integration suite.");

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming requests are not used in this integration suite.");

        private async Task<TResponse> HandleCommand<TResponse>(LogDecisionCommand command, CancellationToken cancellationToken)
        {
            var response = await _handler.Handle(command, cancellationToken);
            return (TResponse)(object)response;
        }
    }

    private sealed class CapturingIntegrationEventService : IIntegrationEventService, IEventLogReader
    {
        private readonly Func<IntegrationEvent, bool>? _shouldFailInitialPublish;
        private readonly List<IntegrationEvent> _publishedEvents = [];
        private readonly Dictionary<Guid, IntegrationEvent> _eventsById = [];
        private readonly Dictionary<Guid, IntegrationEventStatusSnapshot> _statuses = [];
        private readonly object _gate = new();

        public CapturingIntegrationEventService(Func<IntegrationEvent, bool>? shouldFailInitialPublish = null)
        {
            _shouldFailInitialPublish = shouldFailInitialPublish;
        }

        public IReadOnlyList<IntegrationEvent> PublishedEvents
        {
            get
            {
                lock (_gate)
                    return _publishedEvents.ToList();
            }
        }

        public Task SaveAndPublish(IDbContext context, IntegrationEvent evt)
        {
            context.SaveChanges();
            lock (_gate)
            {
                _eventsById[evt.Id] = evt;
                bool failInitialPublish = _shouldFailInitialPublish?.Invoke(evt) == true;
                _statuses[evt.Id] = new IntegrationEventStatusSnapshot(
                    evt.Id,
                    failInitialPublish ? EventStateEnum.PublishedFailed : EventStateEnum.Published,
                    1,
                    evt.CreationDate);
                if (!failInitialPublish)
                    _publishedEvents.Add(evt);
            }

            return Task.CompletedTask;
        }

        public void PromoteFailedEventsToPublished(params Type[] eventTypes)
        {
            lock (_gate)
            {
                var filter = eventTypes.Length == 0
                    ? _eventsById.Values.Where(evt => _statuses.GetValueOrDefault(evt.Id).State != EventStateEnum.Published)
                    : _eventsById.Values.Where(evt =>
                        eventTypes.Any(t => t.IsAssignableFrom(evt.GetType()))
                        && _statuses.GetValueOrDefault(evt.Id).State != EventStateEnum.Published);

                foreach (var evt in filter.ToList())
                {
                    var current = _statuses.GetValueOrDefault(evt.Id);
                    _statuses[evt.Id] = new IntegrationEventStatusSnapshot(
                        evt.Id,
                        EventStateEnum.Published,
                        Math.Max(1, current.TimesSent),
                        current.CreationTime == default ? evt.CreationDate : current.CreationTime);
                    if (_publishedEvents.All(p => p.Id != evt.Id))
                        _publishedEvents.Add(evt);
                }
            }
        }

        public Task<List<IntegrationEventLogEntry>> GetRetryableEventsAsync(
            TimeSpan stuckThreshold,
            int maxRetries,
            int batchSize,
            CancellationToken ct)
            => Task.FromResult(new List<IntegrationEventLogEntry>());

        public Task<IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot>> GetEventStatusSnapshotsAsync(
            IReadOnlyCollection<Guid> eventIds,
            CancellationToken ct)
        {
            lock (_gate)
            {
                IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot> result = _statuses
                    .Where(kv => eventIds.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                return Task.FromResult(result);
            }
        }

        public Task<List<IntegrationEventLogEntry>> GetStalePublishedEventsAsync(
            TimeSpan staleThreshold,
            int batchSize,
            CancellationToken ct)
            => Task.FromResult(new List<IntegrationEventLogEntry>());

        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TestDistributedLock : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(new Releaser());

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
            => TryAcquireAsync(lockKey, ct);

        private sealed class Releaser : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NonWaitingSingleLeaseDistributedLock : IDistributedLock
    {
        private int _held;

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
        {
            if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
                return Task.FromResult<IAsyncDisposable?>(null);

            return Task.FromResult<IAsyncDisposable?>(new Releaser(this));
        }

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
            => TryAcquireAsync(lockKey, ct);

        private sealed class Releaser : IAsyncDisposable
        {
            private readonly NonWaitingSingleLeaseDistributedLock _owner;
            private int _disposed;

            public Releaser(NonWaitingSingleLeaseDistributedLock owner)
            {
                _owner = owner;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Exchange(ref _owner._held, 0);

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
                meter.Dispose();
        }
    }
}
