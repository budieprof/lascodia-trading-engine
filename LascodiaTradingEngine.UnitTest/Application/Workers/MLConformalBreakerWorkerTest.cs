using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Application;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLConformalBreakerWorkerTest
{
    [Fact]
    public void EvaluateConformalCoverage_Computes_True_Conformal_Coverage()
    {
        var observations = new[]
        {
            new ConformalObservation(true),
            new ConformalObservation(true),
            new ConformalObservation(true),
            new ConformalObservation(false)
        };

        var result = EvaluateConformalCoverage(
            observations,
            targetCoverage: 0.75,
            coverageTolerance: 0.0,
            minLogs: 1,
            triggerRunLength: 2);

        Assert.Equal(4, result.SampleCount);
        Assert.Equal(0.75, result.EmpiricalCoverage, precision: 6);
        Assert.Equal(1, result.ConsecutivePoorCoverageBars);
        Assert.False(result.ShouldTrip);
        Assert.False(result.TrippedByCoverageFloor);
    }

    [Fact]
    public void EvaluateConformalCoverage_Trips_On_Consecutive_Uncovered_Outcomes()
    {
        var observations = new[]
        {
            new ConformalObservation(true),
            new ConformalObservation(false),
            new ConformalObservation(false),
            new ConformalObservation(false),
            new ConformalObservation(true)
        };

        var result = EvaluateConformalCoverage(
            observations,
            targetCoverage: 0.90,
            coverageTolerance: 0.05,
            minLogs: 5,
            triggerRunLength: 3);

        Assert.Equal(0.4, result.EmpiricalCoverage, precision: 6);
        Assert.Equal(3, result.ConsecutivePoorCoverageBars);
        Assert.True(result.HasEnoughSamples);
        Assert.True(result.ShouldTrip);
        Assert.True(result.TrippedByCoverageFloor);
        Assert.Equal(MLConformalBreakerTripReason.Both, result.TripReason);
    }

    [Fact]
    public void EvaluateConformalCoverage_Trips_On_Sustained_Low_Coverage_Without_Long_Run()
    {
        var observations = new[]
        {
            new ConformalObservation(true),
            new ConformalObservation(false),
            new ConformalObservation(true),
            new ConformalObservation(false),
            new ConformalObservation(true),
            new ConformalObservation(false),
            new ConformalObservation(true),
            new ConformalObservation(false),
            new ConformalObservation(true),
            new ConformalObservation(false)
        };

        var result = EvaluateConformalCoverage(
            observations,
            targetCoverage: 0.90,
            coverageTolerance: 0.05,
            minLogs: 10,
            triggerRunLength: 3);

        Assert.Equal(0.5, result.EmpiricalCoverage, precision: 6);
        Assert.Equal(1, result.ConsecutivePoorCoverageBars);
        Assert.True(result.ShouldTrip);
        Assert.True(result.TrippedByCoverageFloor);
        Assert.Equal(MLConformalBreakerTripReason.SustainedLowCoverage, result.TripReason);
    }

    [Fact]
    public void EvaluateConformalCoverage_Does_Not_Trip_Before_Minimum_Samples()
    {
        var observations = new[]
        {
            new ConformalObservation(false),
            new ConformalObservation(false),
            new ConformalObservation(false)
        };

        var result = EvaluateConformalCoverage(
            observations,
            targetCoverage: 0.90,
            coverageTolerance: 0.05,
            minLogs: 4,
            triggerRunLength: 3);

        Assert.Equal(3, result.ConsecutivePoorCoverageBars);
        Assert.False(result.HasEnoughSamples);
        Assert.False(result.ShouldTrip);
    }

    [Fact]
    public void EvaluateConformalCoverage_Does_Not_Trip_When_Empirical_Coverage_Meets_Floor()
    {
        var observations = Enumerable.Range(0, 30)
            .Select(i => new ConformalObservation(i < 27))
            .ToArray();

        var result = EvaluateConformalCoverage(
            observations,
            targetCoverage: 0.90,
            coverageTolerance: 0.05,
            minLogs: 30,
            triggerRunLength: 8);

        Assert.Equal(0.90, result.EmpiricalCoverage, precision: 6);
        Assert.False(result.ShouldTrip);
        Assert.Equal(MLConformalBreakerTripReason.Unknown, result.TripReason);
    }

    [Fact]
    public void EvaluateConformalCoverage_Uses_Continuous_Wilson_Confidence_Level()
    {
        var observations = Enumerable.Range(0, 100)
            .Select(i => new ConformalObservation(i < 84))
            .ToArray();

        var lowerConfidence = EvaluateConformalCoverage(
            observations,
            targetCoverage: 0.90,
            minLogs: 30,
            triggerRunLength: 20,
            wilsonConfidenceLevel: 0.90);
        var higherConfidence = EvaluateConformalCoverage(
            observations,
            targetCoverage: 0.90,
            minLogs: 30,
            triggerRunLength: 20,
            wilsonConfidenceLevel: 0.99);

        Assert.True(higherConfidence.CoverageUpperBound > lowerConfidence.CoverageUpperBound);
        Assert.True(higherConfidence.CoverageLowerBound < lowerConfidence.CoverageLowerBound);
    }

    [Fact]
    public void GetBarDuration_Uses_Actual_Timeframe_Length()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), MLConformalBreakerWorker.GetBarDuration(Timeframe.M1));
        Assert.Equal(TimeSpan.FromMinutes(5), MLConformalBreakerWorker.GetBarDuration(Timeframe.M5));
        Assert.Equal(TimeSpan.FromMinutes(15), MLConformalBreakerWorker.GetBarDuration(Timeframe.M15));
        Assert.Equal(TimeSpan.FromHours(1), MLConformalBreakerWorker.GetBarDuration(Timeframe.H1));
        Assert.Equal(TimeSpan.FromHours(4), MLConformalBreakerWorker.GetBarDuration(Timeframe.H4));
        Assert.Equal(TimeSpan.FromDays(1), MLConformalBreakerWorker.GetBarDuration(Timeframe.D1));
    }

    [Fact]
    public async Task RunAsync_Recovers_Active_Breaker_And_Lifts_Suppression()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: true);
        var breaker = CreateBreaker(model.Id, suspendedAt: now.AddHours(-2), resumeAt: now.AddHours(8));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalBreakerLog>().Add(breaker);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, breaker.SuspendedAt.AddMinutes(1), covered: true, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());
        await worker.RunAsync(CancellationToken.None);

        Assert.False(await db.Set<MLConformalBreakerLog>().Where(b => b.Id == breaker.Id).Select(b => b.IsActive).SingleAsync());
        Assert.False(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());
    }

    [Fact]
    public async Task RunAsync_Does_Not_Lift_Suppression_When_Kelly_Guard_Remains_Active()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: true);
        var breaker = CreateBreaker(model.Id, suspendedAt: now.AddHours(-2), resumeAt: now.AddHours(8));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalBreakerLog>().Add(breaker);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, breaker.SuspendedAt.AddMinutes(1), covered: true, startTradeSignalId: 1);
        db.Set<MLKellyFractionLog>().Add(new MLKellyFractionLog
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe.ToString(),
            NegativeEV = true,
            ComputedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());
        await worker.RunAsync(CancellationToken.None);

        Assert.False(await db.Set<MLConformalBreakerLog>().Where(b => b.Id == breaker.Id).Select(b => b.IsActive).SingleAsync());
        Assert.True(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());
    }

    [Fact]
    public async Task RunAsync_Does_Not_Extend_Active_Breaker_From_Stale_PreSuspension_Evidence()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: true);
        var breaker = CreateBreaker(model.Id, suspendedAt: now.AddHours(-2), resumeAt: now.AddHours(8));
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        db.Set<MLConformalBreakerLog>().Add(breaker);
        AddPredictionLogs(db, model, breaker.SuspendedAt.AddMinutes(-30), covered: false, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var originalResumeAt = breaker.ResumeAt;
        var worker = CreateWorker(db, CreateBreakerOptions());

        await worker.RunAsync(CancellationToken.None);

        var storedBreaker = await db.Set<MLConformalBreakerLog>().SingleAsync(b => b.Id == breaker.Id);
        Assert.True(storedBreaker.IsActive);
        Assert.Equal(originalResumeAt, storedBreaker.ResumeAt);
    }

    [Fact]
    public async Task RunAsync_Refreshes_Active_Breaker_Diagnostics_Without_Extending_Suspension()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: true);
        var breaker = CreateBreaker(model.Id, suspendedAt: now.AddHours(-2), resumeAt: now.AddHours(8));
        var firstOutcomeAt = breaker.SuspendedAt.AddMinutes(1);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        db.Set<MLConformalBreakerLog>().Add(breaker);
        AddPredictionLogs(db, model, firstOutcomeAt, covered: false, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var originalSuspendedAt = breaker.SuspendedAt;
        var originalResumeAt = breaker.ResumeAt;
        var worker = CreateWorker(db, CreateBreakerOptions());

        await worker.RunAsync(CancellationToken.None);

        var storedBreaker = await db.Set<MLConformalBreakerLog>().SingleAsync(b => b.Id == breaker.Id);
        Assert.True(storedBreaker.IsActive);
        Assert.Equal(originalSuspendedAt, storedBreaker.SuspendedAt);
        Assert.Equal(originalResumeAt, storedBreaker.ResumeAt);
        Assert.Equal(30, storedBreaker.SampleCount);
        Assert.Equal(30, storedBreaker.FreshSampleCount);
        Assert.Equal(0, storedBreaker.CoveredCount);
        Assert.Equal(0.0, storedBreaker.EmpiricalCoverage, precision: 6);
        Assert.NotNull(storedBreaker.CoverageUpperBound);
        Assert.Equal(firstOutcomeAt.AddMinutes(29), storedBreaker.LastEvaluatedOutcomeAt);
        Assert.Equal(MLConformalBreakerTripReason.Both, storedBreaker.TripReason);
    }

    [Fact]
    public async Task RunAsync_Recovers_Duplicate_Active_Breakers_For_Same_Model()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: true);
        var olderBreaker = CreateBreaker(model.Id, id: 10, suspendedAt: now.AddHours(-3), resumeAt: now.AddHours(8));
        var newerBreaker = CreateBreaker(model.Id, id: 11, suspendedAt: now.AddHours(-2), resumeAt: now.AddHours(8));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalBreakerLog>().AddRange(olderBreaker, newerBreaker);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, newerBreaker.SuspendedAt.AddMinutes(1), covered: true, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());
        await worker.RunAsync(CancellationToken.None);

        var breakers = await db.Set<MLConformalBreakerLog>().OrderBy(b => b.Id).ToListAsync();
        Assert.All(breakers, b => Assert.False(b.IsActive));
        Assert.False(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());
    }

    [Fact]
    public async Task RunAsync_Skips_Mismatched_Calibration_For_Model()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: false);
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalCalibration>().Add(new MLConformalCalibration
        {
            MLModelId = model.Id,
            Symbol = "GBPUSD",
            Timeframe = model.Timeframe,
            CalibrationSamples = 30,
            TargetCoverage = 0.90,
            CoverageThreshold = 0.50,
            CalibratedAt = now.AddDays(-1)
        });
        AddPredictionLogs(db, model, now.AddHours(-2), covered: false, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());
        await worker.RunAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLConformalBreakerLog>().ToListAsync());
        Assert.False(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());
    }

    [Fact]
    public async Task RunAsync_Creates_Structured_Trip_Alert_And_Audit_Diagnostics()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: false);
        var firstOutcomeAt = now.AddHours(-2);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, firstOutcomeAt, covered: false, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var dispatcher = new CapturingAlertDispatcher();
        var worker = CreateWorker(db, CreateBreakerOptions(), dispatcher);

        await worker.RunAsync(CancellationToken.None);

        var breaker = await db.Set<MLConformalBreakerLog>().SingleAsync();
        Assert.True(breaker.IsActive);
        Assert.Equal(30, breaker.FreshSampleCount);
        Assert.NotNull(breaker.CoverageUpperBound);
        Assert.Equal(firstOutcomeAt.AddMinutes(29), breaker.LastEvaluatedOutcomeAt);
        Assert.True(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());

        var alert = Assert.Single(await db.Set<Alert>().ToListAsync());
        Assert.Same(alert, Assert.Single(dispatcher.Dispatched).Alert);
        using var payload = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal(model.Id, payload.RootElement.GetProperty("ModelId").GetInt64());
        Assert.Equal("Both", payload.RootElement.GetProperty("Reason").GetString());
        Assert.Equal(0.0, payload.RootElement.GetProperty("EmpiricalCoverage").GetDouble(), precision: 6);
        Assert.True(payload.RootElement.TryGetProperty("CoverageUpperBound", out _));
        Assert.Equal(firstOutcomeAt.AddMinutes(29), payload.RootElement.GetProperty("LastEvaluatedOutcomeAt").GetDateTime());
    }

    [Fact]
    public async Task RunAsync_Persists_Breaker_When_Alert_Dispatch_Fails()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-2), covered: false, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions(), new ThrowingAlertDispatcher());

        await worker.RunAsync(CancellationToken.None);

        var breaker = await db.Set<MLConformalBreakerLog>().SingleAsync();
        Assert.True(breaker.IsActive);
        Assert.True(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());
        Assert.Single(await db.Set<Alert>().ToListAsync());
    }

    [Fact]
    public async Task RunAsync_Expired_Breaker_Does_Not_Lift_Suppression_When_Kelly_Guard_Remains_Active()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: true);
        var breaker = CreateBreaker(model.Id, suspendedAt: now.AddHours(-6), resumeAt: now.AddMinutes(-1));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalBreakerLog>().Add(breaker);
        db.Set<MLKellyFractionLog>().Add(new MLKellyFractionLog
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe.ToString(),
            NegativeEV = true,
            ComputedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());

        await worker.RunAsync(CancellationToken.None);

        Assert.False(await db.Set<MLConformalBreakerLog>().Where(b => b.Id == breaker.Id).Select(b => b.IsActive).SingleAsync());
        Assert.True(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());
    }

    [Fact]
    public async Task RunAsync_Processes_Trip_Recovery_And_Skip_In_One_Cycle()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var tripModel = CreateModel(1, "EURUSD", isSuppressed: false);
        var recoveryModel = CreateModel(2, "GBPUSD", isSuppressed: true);
        var skippedModel = CreateModel(3, "USDJPY", isSuppressed: false);
        var activeBreaker = CreateBreaker(recoveryModel.Id, id: 20, symbol: recoveryModel.Symbol, suspendedAt: now.AddHours(-2), resumeAt: now.AddHours(8));
        db.Set<MLModel>().AddRange(tripModel, recoveryModel, skippedModel);
        db.Set<MLConformalBreakerLog>().Add(activeBreaker);
        AddCalibration(db, tripModel, now);
        AddCalibration(db, recoveryModel, now);
        AddPredictionLogs(db, tripModel, now.AddHours(-2), covered: false, startTradeSignalId: 1);
        AddPredictionLogs(db, recoveryModel, activeBreaker.SuspendedAt.AddMinutes(1), covered: true, startTradeSignalId: 100);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());

        await worker.RunAsync(CancellationToken.None);

        var breakers = await db.Set<MLConformalBreakerLog>()
            .OrderBy(b => b.MLModelId)
            .ToListAsync();
        Assert.Equal(2, breakers.Count);
        Assert.True(breakers.Single(b => b.MLModelId == tripModel.Id).IsActive);
        Assert.False(breakers.Single(b => b.MLModelId == recoveryModel.Id).IsActive);
        Assert.True(await db.Set<MLModel>().Where(m => m.Id == tripModel.Id).Select(m => m.IsSuppressed).SingleAsync());
        Assert.False(await db.Set<MLModel>().Where(m => m.Id == recoveryModel.Id).Select(m => m.IsSuppressed).SingleAsync());
        Assert.False(await db.Set<MLModel>().Where(m => m.Id == skippedModel.Id).Select(m => m.IsSuppressed).SingleAsync());
    }

    [Fact]
    public void ConfigureApplicationServices_Wires_ConformalBreaker_HostedService_Dependencies_And_ValidatedOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MLConformalBreakerOptions:InitialDelayMinutes"] = "0",
                ["MLConformalBreakerOptions:PollIntervalHours"] = "24",
                ["MLConformalBreakerOptions:MinLogs"] = "30",
                ["MLConformalBreakerOptions:MaxLogs"] = "200"
            })
            .Build();
        var services = new ServiceCollection();

        services.BindConfigurationOptions(configuration);
        services.ConfigureApplicationServices(configuration);

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(MLConformalBreakerWorker));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IMLConformalCoverageEvaluator)
            && d.ImplementationType == typeof(MLConformalCoverageEvaluator));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IMLConformalPredictionLogReader)
            && d.ImplementationType == typeof(MLConformalPredictionLogReader));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IMLConformalCalibrationReader)
            && d.ImplementationType == typeof(MLConformalCalibrationReader));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IMLConformalBreakerStateStore)
            && d.ImplementationType == typeof(MLConformalBreakerStateStore));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IValidateOptions<MLConformalBreakerOptions>)
            && d.ImplementationType == typeof(MLConformalBreakerOptionsValidator));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(MLConformalBreakerOptions)
            && d.ImplementationFactory is not null);
    }

    [Fact]
    public void ConfigureInfrastructureServices_Validates_ConformalBreakerOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MLConformalBreakerOptions:MinLogs"] = "9",
                ["MLConformalBreakerOptions:MaxLogs"] = "8"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.ConfigureInfrastructureServices(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<MLConformalBreakerOptions>>();
        var ex = Assert.Throws<OptionsValidationException>(() => _ = options.Value);
        Assert.Contains("MinLogs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MaxLogs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Skips_When_Distributed_Lock_Is_Busy()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-2), covered: false, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var distributedLock = new Mock<IDistributedLock>();
        distributedLock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var worker = CreateWorker(db, CreateBreakerOptions(), distributedLock: distributedLock.Object);
        await worker.RunAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLConformalBreakerLog>().ToListAsync());
        Assert.False(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());
    }

    [Fact]
    public async Task RunAsync_Skips_Calibration_Before_Model_Activation()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: false);
        model.ActivatedAt = now;
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalCalibration>().Add(new MLConformalCalibration
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            CalibrationSamples = 30,
            TargetCoverage = 0.90,
            CoverageThreshold = 0.50,
            CalibratedAt = now.AddMinutes(-1)
        });
        AddPredictionLogs(db, model, now.AddHours(-2), covered: false, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());
        await worker.RunAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLConformalBreakerLog>().ToListAsync());
        Assert.False(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());
    }

    [Fact]
    public async Task RunAsync_Processes_Multiple_Model_Batches()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var firstModel = CreateModel(1, "EURUSD", isSuppressed: false);
        var secondModel = CreateModel(2, "GBPUSD", isSuppressed: false);
        db.Set<MLModel>().AddRange(firstModel, secondModel);
        AddCalibration(db, firstModel, now);
        AddCalibration(db, secondModel, now);
        AddPredictionLogs(db, firstModel, now.AddHours(-2), covered: false, startTradeSignalId: 1);
        AddPredictionLogs(db, secondModel, now.AddHours(-2), covered: false, startTradeSignalId: 100);
        await db.SaveChangesAsync();

        var options = CreateBreakerOptions();
        options.ModelBatchSize = 1;
        options.MaxCycleModels = 2;
        var worker = CreateWorker(db, options);
        await worker.RunAsync(CancellationToken.None);

        var breakers = await db.Set<MLConformalBreakerLog>().ToListAsync();
        Assert.Equal(2, breakers.Count);
        Assert.All(breakers, b => Assert.True(b.IsActive));
    }

    [Fact]
    public async Task RunAsync_Expired_Breaker_Can_Retrip_In_Same_Cycle()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: true);
        var expiredBreaker = CreateBreaker(model.Id, id: 40, suspendedAt: now.AddHours(-6), resumeAt: now.AddMinutes(-1));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalBreakerLog>().Add(expiredBreaker);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddMinutes(-30), covered: false, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions(), new CapturingAlertDispatcher());
        await worker.RunAsync(CancellationToken.None);

        var breakers = await db.Set<MLConformalBreakerLog>()
            .OrderBy(b => b.Id)
            .ToListAsync();

        Assert.Equal(2, breakers.Count);
        Assert.False(breakers.Single(b => b.Id == expiredBreaker.Id).IsActive);
        Assert.True(breakers.Single(b => b.Id != expiredBreaker.Id).IsActive);
        Assert.True(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());
    }

    [Fact]
    public async Task RunAsync_Uses_Served_CalibrationId_Threshold_When_Log_Threshold_Is_Missing()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: false);
        db.Set<MLModel>().Add(model);

        db.Set<MLConformalCalibration>().AddRange(
            new MLConformalCalibration
            {
                Id = 200,
                MLModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                CalibrationSamples = 30,
                TargetCoverage = 0.90,
                CoverageThreshold = 0.80,
                CalibratedAt = now.AddDays(-2)
            },
            new MLConformalCalibration
            {
                Id = 201,
                MLModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                CalibrationSamples = 30,
                TargetCoverage = 0.90,
                CoverageThreshold = 0.30,
                CalibratedAt = now.AddDays(-1)
            });

        for (int i = 0; i < 30; i++)
        {
            db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
            {
                MLModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                TradeSignalId = 500 + i,
                ActualDirection = TradeDirection.Buy,
                OutcomeRecordedAt = now.AddMinutes(-60 + i),
                ConformalNonConformityScore = 0.70,
                MLConformalCalibrationId = 200
            });
        }

        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());
        await worker.RunAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLConformalBreakerLog>().ToListAsync());
        Assert.False(await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.IsSuppressed).SingleAsync());
    }

    [Fact]
    public async Task RunAsync_Reuses_Existing_Active_Alert_On_Retrip()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-2), covered: false, startTradeSignalId: 1);
        db.Set<Alert>().Add(new Alert
        {
            Id = 90,
            AlertType = AlertType.MLModelDegraded,
            Symbol = model.Symbol,
            IsActive = true,
            Severity = AlertSeverity.High,
            DeduplicationKey = $"MLConformalBreaker:{model.Id}:{model.Symbol}:{model.Timeframe}",
            CooldownSeconds = 3600,
            LastTriggeredAt = now.AddHours(-2),
            ConditionJson = "{}"
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions(), new CapturingAlertDispatcher());
        await worker.RunAsync(CancellationToken.None);

        var alerts = await db.Set<Alert>()
            .OrderBy(a => a.Id)
            .ToListAsync();
        Assert.Single(alerts);
        Assert.True(alerts[0].IsActive);
    }

    [Fact]
    public async Task RunAsync_Recovery_Resolves_Active_Alert()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(isSuppressed: true);
        var breaker = CreateBreaker(model.Id, suspendedAt: now.AddHours(-2), resumeAt: now.AddHours(8));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalBreakerLog>().Add(breaker);
        db.Set<Alert>().Add(new Alert
        {
            Id = 91,
            AlertType = AlertType.MLModelDegraded,
            Symbol = model.Symbol,
            IsActive = true,
            Severity = AlertSeverity.High,
            DeduplicationKey = $"MLConformalBreaker:{model.Id}:{model.Symbol}:{model.Timeframe}",
            CooldownSeconds = 3600,
            LastTriggeredAt = now.AddMinutes(-5),
            ConditionJson = "{}"
        });
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, breaker.SuspendedAt.AddMinutes(1), covered: true, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions(), new CapturingAlertDispatcher());
        await worker.RunAsync(CancellationToken.None);

        var alert = await db.Set<Alert>().SingleAsync(a => a.Id == 91);
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunAsync_AlertBackpressure_HaltsTripDispatchesPastBudget()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var modelA = CreateModel(1, "EURUSD", isSuppressed: false);
        var modelB = CreateModel(2, "GBPUSD", isSuppressed: false);
        var modelC = CreateModel(3, "USDJPY", isSuppressed: false);
        db.Set<MLModel>().AddRange(modelA, modelB, modelC);
        AddCalibration(db, modelA, now);
        AddCalibration(db, modelB, now);
        AddCalibration(db, modelC, now);
        // All three are uncovered → all three should trip absent backpressure.
        AddPredictionLogs(db, modelA, now.AddHours(-2), covered: false, startTradeSignalId: 1);
        AddPredictionLogs(db, modelB, now.AddHours(-2), covered: false, startTradeSignalId: 100);
        AddPredictionLogs(db, modelC, now.AddHours(-2), covered: false, startTradeSignalId: 200);
        await db.SaveChangesAsync();

        var dispatcher = new CapturingAlertDispatcher();
        var options = CreateBreakerOptions();
        options.MaxAlertsPerCycle = 1;

        var worker = CreateWorker(db, options, alertDispatcher: dispatcher);
        await worker.RunAsync(CancellationToken.None);

        // All three break, but only one alert is dispatched.
        var trippedBreakers = await db.Set<MLConformalBreakerLog>().Where(b => b.IsActive).ToListAsync();
        Assert.Equal(3, trippedBreakers.Count);
        Assert.Single(dispatcher.Dispatched);
    }

    [Fact]
    public async Task RunAsync_FairRotation_PrefersLeastRecentlyEvaluatedModelsWhenOverCap()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var staleModel = CreateModel(1, "EURUSD", isSuppressed: false);
        var freshModel = CreateModel(2, "GBPUSD", isSuppressed: false);
        db.Set<MLModel>().AddRange(staleModel, freshModel);
        AddCalibration(db, staleModel, now);
        AddCalibration(db, freshModel, now);
        AddPredictionLogs(db, staleModel, now.AddHours(-2), covered: false, startTradeSignalId: 1);
        AddPredictionLogs(db, freshModel, now.AddHours(-2), covered: false, startTradeSignalId: 100);

        // freshModel was just evaluated; staleModel hasn't been evaluated. With cap=1,
        // staleModel should be picked first. Pre-seed only freshModel's cursor.
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLConformal:Model:2:LastEvaluatedAt",
            Value = now.AddHours(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            DataType = ConfigDataType.String,
            IsHotReloadable = false,
            LastUpdatedAt = now,
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var options = CreateBreakerOptions();
        options.MaxCycleModels = 1;
        options.ModelBatchSize = 1;

        var worker = CreateWorker(db, options);
        await worker.RunAsync(CancellationToken.None);

        var trippedBreakers = await db.Set<MLConformalBreakerLog>().Where(b => b.IsActive).ToListAsync();
        var tripped = Assert.Single(trippedBreakers);
        Assert.Equal(staleModel.Id, tripped.MLModelId);
    }

    [Fact]
    public async Task RunAsync_PerModelCursors_ArePersistedForEvaluatedModels()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD", isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-2), covered: false, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());
        await worker.RunAsync(CancellationToken.None);

        var lastEvaluatedConfig = await db.Set<EngineConfig>()
            .SingleOrDefaultAsync(c => c.Key == "MLConformal:Model:1:LastEvaluatedAt");
        Assert.NotNull(lastEvaluatedConfig);

        var tripStreakConfig = await db.Set<EngineConfig>()
            .SingleOrDefaultAsync(c => c.Key == "MLConformal:Model:1:TripStreak");
        Assert.NotNull(tripStreakConfig);
        Assert.Equal("1", tripStreakConfig!.Value);
    }

    [Fact]
    public async Task RunAsync_ChronicTripEscalation_FiresAlertOnceWhenStreakReachesThreshold()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD", isSuppressed: true);
        var existingBreaker = CreateBreaker(model.Id, id: 50, suspendedAt: now.AddHours(-2), resumeAt: now.AddHours(8));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalBreakerLog>().Add(existingBreaker);
        AddCalibration(db, model, now);
        // Logs after the suspension started → the active breaker should be refreshed (still bad).
        AddPredictionLogs(db, model, existingBreaker.SuspendedAt.AddMinutes(1), covered: false, startTradeSignalId: 1);

        // Pre-seed streak=3 (one short of the default ChronicTripThreshold=4).
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLConformal:Model:1:TripStreak",
            Value = "3",
            DataType = ConfigDataType.Int,
            IsHotReloadable = false,
            LastUpdatedAt = now,
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var dispatcher = new CapturingAlertDispatcher();
        var worker = CreateWorker(db, CreateBreakerOptions(), alertDispatcher: dispatcher);
        await worker.RunAsync(CancellationToken.None);

        // Streak crossed threshold → chronic alert fired.
        var chronicAlert = await db.Set<Alert>()
            .SingleOrDefaultAsync(a => a.DeduplicationKey == "ml-conformal-chronic-trip:1");
        Assert.NotNull(chronicAlert);
        Assert.True(chronicAlert!.IsActive);
        Assert.Equal(AlertType.MLModelDegraded, chronicAlert.AlertType);

        // Dispatcher saw the chronic message.
        Assert.Contains(
            dispatcher.Dispatched,
            d => d.Alert.DeduplicationKey == "ml-conformal-chronic-trip:1");

        // Streak is now 4.
        var streakConfig = await db.Set<EngineConfig>()
            .SingleAsync(c => c.Key == "MLConformal:Model:1:TripStreak");
        Assert.Equal("4", streakConfig.Value);
    }

    [Fact]
    public async Task RunAsync_ChronicTripEscalation_AutoResolvesOnRecovery()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD", isSuppressed: true);
        var existingBreaker = CreateBreaker(model.Id, id: 60, suspendedAt: now.AddHours(-2), resumeAt: now.AddHours(8));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalBreakerLog>().Add(existingBreaker);
        AddCalibration(db, model, now);
        // Logs after the suspension that show recovery → covered.
        AddPredictionLogs(db, model, existingBreaker.SuspendedAt.AddMinutes(1), covered: true, startTradeSignalId: 1);

        // Pre-seed an active chronic alert + streak.
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLConformal:Model:1:TripStreak",
            Value = "5",
            DataType = ConfigDataType.Int,
            IsHotReloadable = false,
            LastUpdatedAt = now,
            IsDeleted = false
        });
        db.Set<Alert>().Add(new Alert
        {
            Id = 9090,
            AlertType = AlertType.MLModelDegraded,
            DeduplicationKey = "ml-conformal-chronic-trip:1",
            Symbol = "EURUSD",
            Severity = AlertSeverity.High,
            CooldownSeconds = 3600,
            ConditionJson = "{}",
            IsActive = true,
            LastTriggeredAt = now.AddHours(-1),
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());
        await worker.RunAsync(CancellationToken.None);

        var chronicAlert = await db.Set<Alert>()
            .SingleAsync(a => a.DeduplicationKey == "ml-conformal-chronic-trip:1");
        Assert.False(chronicAlert.IsActive);
        Assert.NotNull(chronicAlert.AutoResolvedAt);

        // Streak reset.
        var streakConfig = await db.Set<EngineConfig>()
            .SingleAsync(c => c.Key == "MLConformal:Model:1:TripStreak");
        Assert.Equal("0", streakConfig.Value);
    }

    [Fact]
    public void EvaluateConformalCoverage_TimeDecay_DownweightsOldObservations()
    {
        // Half the observations are very old uncovered, half are recent covered. With
        // unweighted coverage the empirical comes out 0.5; with a 7-day half-life the
        // recent (covered) ones dominate and weighted coverage > 0.7. This is the
        // "model just recovered" scenario the worker is supposed to handle gracefully.
        var now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var observations = new List<ConformalObservation>();
        for (int i = 0; i < 30; i++)
            observations.Add(new ConformalObservation(false, now.AddDays(-30 - i)));
        for (int i = 0; i < 30; i++)
            observations.Add(new ConformalObservation(true, now.AddHours(-i)));

        var unweighted = new MLConformalCoverageEvaluator().Evaluate(
            observations,
            new ConformalCoverageEvaluationOptions(
                TargetCoverage: 0.90,
                CoverageTolerance: 0.05,
                MinLogs: 30,
                TriggerRunLength: 100,
                UseWilsonCoverageFloor: true,
                WilsonConfidenceLevel: 0.95,
                StatisticalAlpha: 0.01,
                TimeDecayHalfLifeDays: 0,
                BootstrapResamples: 0,
                RegressionGuardK: 0.0,
                ModelId: 0,
                NowUtc: now));

        var weighted = new MLConformalCoverageEvaluator().Evaluate(
            observations,
            new ConformalCoverageEvaluationOptions(
                TargetCoverage: 0.90,
                CoverageTolerance: 0.05,
                MinLogs: 30,
                TriggerRunLength: 100,
                UseWilsonCoverageFloor: true,
                WilsonConfidenceLevel: 0.95,
                StatisticalAlpha: 0.01,
                TimeDecayHalfLifeDays: 7,
                BootstrapResamples: 0,
                RegressionGuardK: 0.0,
                ModelId: 0,
                NowUtc: now));

        Assert.Equal(0.5, unweighted.EmpiricalCoverage, precision: 6);
        Assert.Equal(0.5, unweighted.TimeDecayWeightedCoverage, precision: 6); // half-life=0 → unweighted
        Assert.True(weighted.TimeDecayWeightedCoverage > 0.95,
            $"expected recent covered observations to dominate; weighted={weighted.TimeDecayWeightedCoverage}");
    }

    [Fact]
    public void EvaluateConformalCoverage_BootstrapStderr_IsDeterministicForSameInput()
    {
        // Two evaluator calls with the same observations + modelId must produce the same
        // stderr. This is what guarantees two replicas observing identical logs compute
        // identical K-sigma trip decisions.
        var now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var observations = new List<ConformalObservation>();
        for (int i = 0; i < 50; i++)
            observations.Add(new ConformalObservation(i % 3 != 0, now.AddMinutes(-i))); // 2/3 covered

        var first = new MLConformalCoverageEvaluator().Evaluate(
            observations,
            new ConformalCoverageEvaluationOptions(
                TargetCoverage: 0.90,
                CoverageTolerance: 0.05,
                MinLogs: 30,
                TriggerRunLength: 100,
                UseWilsonCoverageFloor: true,
                WilsonConfidenceLevel: 0.95,
                StatisticalAlpha: 0.01,
                TimeDecayHalfLifeDays: 0,
                BootstrapResamples: 200,
                RegressionGuardK: 0.0,
                ModelId: 42,
                NowUtc: now));

        var second = new MLConformalCoverageEvaluator().Evaluate(
            observations,
            new ConformalCoverageEvaluationOptions(
                TargetCoverage: 0.90,
                CoverageTolerance: 0.05,
                MinLogs: 30,
                TriggerRunLength: 100,
                UseWilsonCoverageFloor: true,
                WilsonConfidenceLevel: 0.95,
                StatisticalAlpha: 0.01,
                TimeDecayHalfLifeDays: 0,
                BootstrapResamples: 200,
                RegressionGuardK: 0.0,
                ModelId: 42,
                NowUtc: now));

        Assert.Equal(200, first.BootstrapResamplesUsed);
        Assert.Equal(first.CoverageStderr, second.CoverageStderr, precision: 12);
        Assert.True(first.CoverageStderr > 0, "stderr must be positive for non-degenerate observations");
    }

    [Fact]
    public void EvaluateConformalCoverage_KSigmaGate_BlocksTripWhenEmpiricalIsCloseToFloor()
    {
        // Empirical = 0.80, floor = 0.85, K=3. With stderr ~0.04 (typical for n=30,
        // p=0.8), 3-sigma below empirical reaches 0.68 — well below floor. The K-sigma
        // gate (empirical < floor - K*stderr) requires 0.80 < 0.85 - 3*stderr → stderr <
        // -0.0167, impossible. So the K-sigma path blocks the trip even when the floor
        // and Wilson paths would individually fire.
        var now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var observations = new List<ConformalObservation>();
        // 24 covered, 6 uncovered → 0.80 empirical, no long run.
        for (int i = 0; i < 24; i++) observations.Add(new ConformalObservation(true, now.AddMinutes(-i)));
        for (int i = 0; i < 6; i++) observations.Add(new ConformalObservation(false, now.AddMinutes(-(24 + i))));

        var withKSigma = new MLConformalCoverageEvaluator().Evaluate(
            observations,
            new ConformalCoverageEvaluationOptions(
                TargetCoverage: 0.90,
                CoverageTolerance: 0.05,
                MinLogs: 30,
                TriggerRunLength: 100,
                UseWilsonCoverageFloor: false, // use p-value path so we exercise the floor logic
                WilsonConfidenceLevel: 0.95,
                StatisticalAlpha: 0.99, // relaxed → p-value path would fire
                TimeDecayHalfLifeDays: 0,
                BootstrapResamples: 200,
                RegressionGuardK: 3.0,
                ModelId: 1,
                NowUtc: now));

        var withoutKSigma = new MLConformalCoverageEvaluator().Evaluate(
            observations,
            new ConformalCoverageEvaluationOptions(
                TargetCoverage: 0.90,
                CoverageTolerance: 0.05,
                MinLogs: 30,
                TriggerRunLength: 100,
                UseWilsonCoverageFloor: false,
                WilsonConfidenceLevel: 0.95,
                StatisticalAlpha: 0.99,
                TimeDecayHalfLifeDays: 0,
                BootstrapResamples: 0, // disabled → stderr=0 → K-sigma is bypassed
                RegressionGuardK: 0.0,
                ModelId: 1,
                NowUtc: now));

        Assert.False(withKSigma.TrippedByCoverageFloor,
            "K-sigma gate should block the trip when stderr indicates noise");
        Assert.True(withoutKSigma.TrippedByCoverageFloor,
            "without K-sigma, the floor + p-value paths would fire");
    }

    [Fact]
    public async Task RunAsync_OverrideHierarchy_PerModelMinLogsBlocksEvaluation()
    {
        // Without override: 30 uncovered logs trip the breaker. With per-model
        // override MinLogs=100, the model is skipped as insufficient and no breaker
        // is created. Verifies the override hierarchy reaches the evaluator.
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD", isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-1), covered: false, startTradeSignalId: 1);

        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLConformal:Override:Model:1:MinLogs",
            Value = "100",
            DataType = ConfigDataType.Int,
            IsHotReloadable = false,
            LastUpdatedAt = now,
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());
        await worker.RunAsync(CancellationToken.None);

        Assert.False(await db.Set<MLConformalBreakerLog>().AnyAsync(b => b.MLModelId == model.Id));
    }

    [Fact]
    public async Task RunAsync_OverrideHierarchy_UnknownKnobIsIgnored_NoCrash()
    {
        // A typo'd override key (knob suffix not in the allowlist) must be logged-and-
        // ignored. The cycle continues normally and the model trips on its real signal.
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD", isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-1), covered: false, startTradeSignalId: 1);

        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLConformal:Override:Model:1:DefinitelyNotARealKnob",
            Value = "9999",
            DataType = ConfigDataType.Int,
            IsHotReloadable = false,
            LastUpdatedAt = now,
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions());
        await worker.RunAsync(CancellationToken.None);

        Assert.True(await db.Set<MLConformalBreakerLog>().AnyAsync(b => b.MLModelId == model.Id));
    }

    [Fact]
    public async Task RunAsync_FleetSystemicAlert_FiresWhenManyModelsTrip()
    {
        // Three models trip in one cycle; FleetSystemicMinTrippedModels=2,
        // FleetSystemicTripRatioThreshold=0.5. ratio = 3/3 = 1.0 ≥ 0.5 → fleet alert
        // fires once with the SystemicMLDegradation type.
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;

        for (long i = 1; i <= 3; i++)
        {
            var model = CreateModel(i, $"EURUSD{i}", isSuppressed: false);
            db.Set<MLModel>().Add(model);
            AddCalibration(db, model, now);
            AddPredictionLogs(db, model, now.AddHours(-1), covered: false, startTradeSignalId: i * 1000);
        }
        await db.SaveChangesAsync();

        var options = CreateBreakerOptions();
        options.FleetSystemicMinTrippedModels = 2;
        options.FleetSystemicTripRatioThreshold = 0.5;

        var dispatcher = new CapturingAlertDispatcher();
        var worker = CreateWorker(db, options, alertDispatcher: dispatcher);
        await worker.RunAsync(CancellationToken.None);

        var fleetAlert = await db.Set<Alert>()
            .SingleOrDefaultAsync(a => a.DeduplicationKey == "ml-conformal-fleet-systemic");
        Assert.NotNull(fleetAlert);
        Assert.Equal(AlertType.SystemicMLDegradation, fleetAlert!.AlertType);
        Assert.True(fleetAlert.IsActive);
        Assert.Contains(dispatcher.Dispatched,
            d => d.Alert.DeduplicationKey == "ml-conformal-fleet-systemic");
    }

    [Fact]
    public async Task RunAsync_FleetSystemicAlert_AutoResolvesWhenFleetRecovers()
    {
        // Pre-existing active fleet alert; this cycle has zero trips → alert
        // auto-resolves.
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD", isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-1), covered: true, startTradeSignalId: 1);

        db.Set<Alert>().Add(new Alert
        {
            Id = 7777,
            AlertType = AlertType.SystemicMLDegradation,
            DeduplicationKey = "ml-conformal-fleet-systemic",
            Severity = AlertSeverity.High,
            CooldownSeconds = 3600,
            ConditionJson = "{}",
            IsActive = true,
            LastTriggeredAt = now.AddHours(-1),
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var options = CreateBreakerOptions();
        options.FleetSystemicMinTrippedModels = 1;
        options.FleetSystemicTripRatioThreshold = 0.5;

        var worker = CreateWorker(db, options);
        await worker.RunAsync(CancellationToken.None);

        var fleetAlert = await db.Set<Alert>()
            .SingleAsync(a => a.DeduplicationKey == "ml-conformal-fleet-systemic");
        Assert.False(fleetAlert.IsActive);
        Assert.NotNull(fleetAlert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunAsync_StalenessAlert_FiresForModelWithOldOutcomes()
    {
        // Model has 30 covered logs from 60h ago; latest is 60h - 30min ago. With
        // StalenessHours=48 the model is stale → MLMonitoringStale alert fires.
        // No breaker trip because all are covered.
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD", isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-60), covered: true, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var options = CreateBreakerOptions();
        options.StalenessHours = 48;

        var dispatcher = new CapturingAlertDispatcher();
        var worker = CreateWorker(db, options, alertDispatcher: dispatcher);
        await worker.RunAsync(CancellationToken.None);

        var staleAlert = await db.Set<Alert>()
            .SingleOrDefaultAsync(a => a.DeduplicationKey == "ml-conformal-stale:1");
        Assert.NotNull(staleAlert);
        Assert.Equal(AlertType.MLMonitoringStale, staleAlert!.AlertType);
        Assert.True(staleAlert.IsActive);
        Assert.Contains(dispatcher.Dispatched,
            d => d.Alert.DeduplicationKey == "ml-conformal-stale:1");
    }

    [Fact]
    public void EvaluateConformalCoverage_RegimeBreakdown_IsEmptyWhenNoObservationCarriesRegime()
    {
        var observations = new[]
        {
            new ConformalObservation(true),
            new ConformalObservation(false),
            new ConformalObservation(true)
        };

        var result = EvaluateConformalCoverage(observations, minLogs: 1, triggerRunLength: 100);

        Assert.Empty(result.RegimeBreakdown);
        Assert.Null(result.WorstRegime());
    }

    [Fact]
    public void EvaluateConformalCoverage_RegimeBreakdown_AggregatesPerRegime()
    {
        var now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        // Trending: 3 covered, 1 uncovered → 0.75
        // Ranging:  1 covered, 3 uncovered → 0.25 (the "worst")
        var observations = new List<ConformalObservation>
        {
            new(true,  now.AddMinutes(-1), MarketRegime.Trending),
            new(true,  now.AddMinutes(-2), MarketRegime.Trending),
            new(true,  now.AddMinutes(-3), MarketRegime.Trending),
            new(false, now.AddMinutes(-4), MarketRegime.Trending),
            new(true,  now.AddMinutes(-5), MarketRegime.Ranging),
            new(false, now.AddMinutes(-6), MarketRegime.Ranging),
            new(false, now.AddMinutes(-7), MarketRegime.Ranging),
            new(false, now.AddMinutes(-8), MarketRegime.Ranging),
        };

        var result = new MLConformalCoverageEvaluator().Evaluate(
            observations,
            new ConformalCoverageEvaluationOptions(
                TargetCoverage: 0.90,
                CoverageTolerance: 0.05,
                MinLogs: 4,
                TriggerRunLength: 100,
                UseWilsonCoverageFloor: true,
                WilsonConfidenceLevel: 0.95,
                StatisticalAlpha: 0.01,
                TimeDecayHalfLifeDays: 0,
                BootstrapResamples: 0,
                RegressionGuardK: 0.0,
                ModelId: 0,
                NowUtc: now));

        Assert.Equal(2, result.RegimeBreakdown.Count);
        Assert.Equal(0.75, result.RegimeBreakdown[MarketRegime.Trending].EmpiricalCoverage, precision: 6);
        Assert.Equal(0.25, result.RegimeBreakdown[MarketRegime.Ranging].EmpiricalCoverage, precision: 6);

        var worst = result.WorstRegime();
        Assert.NotNull(worst);
        Assert.Equal(MarketRegime.Ranging, worst!.Value.Regime);
        Assert.Equal(4, worst.Value.Breakdown.SampleCount);
    }

    [Fact]
    public async Task RunAsync_PerRegimeDecomposition_AddsWorstRegimeToTripAlertPayload()
    {
        // Worker has EnablePerRegimeDecomposition=true and a single regime snapshot
        // (Trending) covering the eval window. All 30 logs are uncovered → trip fires.
        // The trip alert payload must include WorstRegime=Trending with coverage 0.0.
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD", isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-1), covered: false, startTradeSignalId: 1);

        db.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            Regime = MarketRegime.Trending,
            Confidence = 0.9m,
            ADX = 30m,
            ATR = 0.001m,
            BollingerBandWidth = 0.002m,
            DetectedAt = now.AddDays(-1),
            IsDeleted = false,
        });
        await db.SaveChangesAsync();

        var options = CreateBreakerOptions();
        options.EnablePerRegimeDecomposition = true;

        var worker = CreateWorker(db, options);
        await worker.RunAsync(CancellationToken.None);

        var trip = await db.Set<MLConformalBreakerLog>()
            .SingleAsync(b => b.MLModelId == model.Id);
        Assert.True(trip.IsActive);

        var alert = await db.Set<Alert>()
            .SingleAsync(a => a.DeduplicationKey.StartsWith("MLConformalBreaker:"));
        var payload = JsonSerializer.Deserialize<MLConformalBreakerAlertPayload>(alert.ConditionJson);
        Assert.NotNull(payload);
        Assert.Equal("Trending", payload!.WorstRegime);
        Assert.Equal(0.0, payload.WorstRegimeCoverage!.Value, precision: 6);
        Assert.Equal(30, payload.WorstRegimeSampleCount);
    }

    [Fact]
    public async Task RunAsync_PerRegimeDecomposition_DisabledByDefault_NoRegimeFieldsInPayload()
    {
        // Sanity: with EnablePerRegimeDecomposition=false (default), the trip alert
        // payload's regime fields are null even when MarketRegimeSnapshot rows exist.
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD", isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-1), covered: false, startTradeSignalId: 1);

        db.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            Regime = MarketRegime.Ranging,
            Confidence = 0.9m,
            ADX = 12m,
            ATR = 0.001m,
            BollingerBandWidth = 0.002m,
            DetectedAt = now.AddDays(-1),
            IsDeleted = false,
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, CreateBreakerOptions()); // default: feature off

        await worker.RunAsync(CancellationToken.None);

        var alert = await db.Set<Alert>()
            .SingleAsync(a => a.DeduplicationKey.StartsWith("MLConformalBreaker:"));
        var payload = JsonSerializer.Deserialize<MLConformalBreakerAlertPayload>(alert.ConditionJson);
        Assert.NotNull(payload);
        Assert.Null(payload!.WorstRegime);
        Assert.Null(payload.WorstRegimeCoverage);
        Assert.Null(payload.WorstRegimeSampleCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task RunAsync_ParallelEvaluation_ProducesSameTripSetAsSequential(int parallelism)
    {
        // Five models, all with 30 uncovered logs → all should trip. The set of tripped
        // model IDs must be identical regardless of evaluation parallelism — proves the
        // sink synchronisation doesn't lose any updates and that no race causes a
        // missed/duplicated trip.
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        for (long i = 1; i <= 5; i++)
        {
            var model = CreateModel(i, $"PAIR{i:D2}", isSuppressed: false);
            db.Set<MLModel>().Add(model);
            AddCalibration(db, model, now);
            AddPredictionLogs(db, model, now.AddHours(-1), covered: false, startTradeSignalId: i * 1000);
        }
        await db.SaveChangesAsync();

        var options = CreateBreakerOptions();
        options.EvaluationParallelism = parallelism;

        var worker = CreateWorker(db, options);
        await worker.RunAsync(CancellationToken.None);

        var trippedIds = await db.Set<MLConformalBreakerLog>()
            .Where(b => b.IsActive)
            .Select(b => b.MLModelId)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal(new[] { 1L, 2L, 3L, 4L, 5L }, trippedIds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(33)]
    public void MLConformalBreakerOptionsValidator_RejectsOutOfRangeEvaluationParallelism(int value)
    {
        var options = new MLConformalBreakerOptions { EvaluationParallelism = value };
        var validator = new MLConformalBreakerOptionsValidator();

        var result = validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("EvaluationParallelism"));
    }

    [Fact]
    public async Task RunAsync_StalenessAlert_AutoResolvesOnFreshOutcomes()
    {
        // Pre-existing staleness alert + fresh logs (latest within StalenessHours) →
        // alert auto-resolves.
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD", isSuppressed: false);
        db.Set<MLModel>().Add(model);
        AddCalibration(db, model, now);
        AddPredictionLogs(db, model, now.AddHours(-1), covered: true, startTradeSignalId: 1);

        db.Set<Alert>().Add(new Alert
        {
            Id = 8888,
            AlertType = AlertType.MLMonitoringStale,
            DeduplicationKey = "ml-conformal-stale:1",
            Symbol = "EURUSD",
            Severity = AlertSeverity.Medium,
            CooldownSeconds = 3600,
            ConditionJson = "{}",
            IsActive = true,
            LastTriggeredAt = now.AddHours(-50),
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var options = CreateBreakerOptions();
        options.StalenessHours = 48;

        var worker = CreateWorker(db, options);
        await worker.RunAsync(CancellationToken.None);

        var staleAlert = await db.Set<Alert>()
            .SingleAsync(a => a.DeduplicationKey == "ml-conformal-stale:1");
        Assert.False(staleAlert.IsActive);
        Assert.NotNull(staleAlert.AutoResolvedAt);
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static ConformalCoverageEvaluation EvaluateConformalCoverage(
        IReadOnlyCollection<ConformalObservation> observations,
        double targetCoverage = 0.90,
        double coverageTolerance = 0.05,
        int minLogs = 30,
        int triggerRunLength = 8,
        bool useWilsonCoverageFloor = true,
        double wilsonConfidenceLevel = 0.95,
        double statisticalAlpha = 0.01)
        => new MLConformalCoverageEvaluator().Evaluate(
            observations,
            new ConformalCoverageEvaluationOptions(
                targetCoverage,
                coverageTolerance,
                minLogs,
                triggerRunLength,
                useWilsonCoverageFloor,
                wilsonConfidenceLevel,
                statisticalAlpha,
                TimeDecayHalfLifeDays: 0,
                BootstrapResamples: 0,
                RegressionGuardK: 0.0,
                ModelId: 0,
                NowUtc: DateTime.UtcNow));

    private static MLConformalBreakerWorker CreateWorker(
        WriteApplicationDbContext db,
        MLConformalBreakerOptions? options = null,
        IAlertDispatcher? alertDispatcher = null,
        IDistributedLock? distributedLock = null,
        TimeProvider? timeProvider = null)
    {
        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);

        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLConformalBreakerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLConformalBreakerWorker>>(),
            options ?? new MLConformalBreakerOptions(),
            new TradingMetrics(new TestMeterFactory()),
            alertDispatcher ?? new CapturingAlertDispatcher(),
            new MLConformalCoverageEvaluator(),
            new MLConformalPredictionLogReader(),
            new MLConformalCalibrationReader(),
            new MLConformalBreakerStateStore(
                Mock.Of<ILogger<MLConformalBreakerStateStore>>(),
                new TradingMetrics(new TestMeterFactory()),
                timeProvider),
            new MLConformalChronicTripAlertUpserter(),
            new MLConformalRegimeResolver(),
            distributedLock,
            timeProvider);
    }

    private static MLConformalBreakerOptions CreateBreakerOptions() => new()
    {
        MinLogs = 30,
        MaxLogs = 200,
        ConsecutiveUncoveredTrigger = 3,
        InitialDelayMinutes = 0
    };

    private static void AddCalibration(WriteApplicationDbContext db, MLModel model, DateTime now)
    {
        db.Set<MLConformalCalibration>().Add(new MLConformalCalibration
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            CalibrationSamples = 30,
            TargetCoverage = 0.90,
            CoverageThreshold = 0.50,
            CalibratedAt = now.AddDays(-1)
        });
    }

    private static void AddPredictionLogs(
        WriteApplicationDbContext db,
        MLModel model,
        DateTime firstOutcomeAt,
        bool covered,
        long startTradeSignalId)
    {
        for (int i = 0; i < 30; i++)
        {
            db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
            {
                MLModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                TradeSignalId = startTradeSignalId + i,
                ActualDirection = TradeDirection.Buy,
                OutcomeRecordedAt = firstOutcomeAt.AddMinutes(i),
                WasConformalCovered = covered
            });
        }
    }

    private static MLModel CreateModel(bool isSuppressed) => CreateModel(1, "EURUSD", isSuppressed);

    private static MLModel CreateModel(long id, string symbol, bool isSuppressed) => new()
    {
        Id = id,
        Symbol = symbol,
        Timeframe = Timeframe.H1,
        ModelVersion = "1.0.0",
        FilePath = "/tmp/model.json",
        Status = MLModelStatus.Active,
        IsActive = true,
        IsSuppressed = isSuppressed,
        TrainingSamples = 100,
        TrainedAt = DateTime.UtcNow.AddDays(-10),
        ActivatedAt = DateTime.UtcNow.AddDays(-5)
    };

    private static MLConformalBreakerLog CreateBreaker(
        long modelId,
        long id = 10,
        string symbol = "EURUSD",
        DateTime? suspendedAt = null,
        DateTime? resumeAt = null)
    {
        var start = suspendedAt ?? DateTime.UtcNow.AddHours(-1);
        return new MLConformalBreakerLog
        {
            Id = id,
            MLModelId = modelId,
            Symbol = symbol,
            Timeframe = Timeframe.H1,
            ConsecutivePoorCoverageBars = 8,
            SampleCount = 30,
            CoveredCount = 10,
            EmpiricalCoverage = 0.33,
            TargetCoverage = 0.90,
            CoverageThreshold = 0.50,
            TripReason = MLConformalBreakerTripReason.Both,
            SuspensionBars = 16,
            SuspendedAt = start,
            ResumeAt = resumeAt ?? start.AddHours(16),
            IsActive = true
        };
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);
        public void Dispose() { }
    }

    private sealed class CapturingAlertDispatcher : IAlertDispatcher
    {
        public List<(Alert Alert, string Message)> Dispatched { get; } = [];

        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            alert.LastTriggeredAt = DateTime.UtcNow;
            Dispatched.Add((alert, message));
            return Task.CompletedTask;
        }

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
        {
            if (!conditionStillActive && !alert.AutoResolvedAt.HasValue)
                alert.AutoResolvedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAlertDispatcher : IAlertDispatcher
    {
        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
            => throw new InvalidOperationException("Dispatch failed.");

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
            => Task.CompletedTask;
    }
}
