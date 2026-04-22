using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class CpcPretrainerWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_Skips_When_Disabled()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLCpc:Enabled", "false", ConfigDataType.Bool);
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        var worker = CreateWorker(db, trainer.Object);

        var nextPoll = await worker.RunCycleAsync(CancellationToken.None);

        trainer.Verify(t => t.TrainAsync(
            It.IsAny<string>(), It.IsAny<Timeframe>(), It.IsAny<IReadOnlyList<float[][]>>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.True(nextPoll > 0);
        Assert.Empty(await db.Set<MLCpcEncoder>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Skips_When_Systemic_Pause_Is_Active()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLTraining:SystemicPauseActive", "true", ConfigDataType.Bool);
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        var worker = CreateWorker(db, trainer.Object);

        await worker.RunCycleAsync(CancellationToken.None);

        trainer.Verify(t => t.TrainAsync(
            It.IsAny<string>(), It.IsAny<Timeframe>(), It.IsAny<IReadOnlyList<float[][]>>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_No_Op_When_No_Active_Models()
    {
        await using var db = CreateDbContext();
        // No active models → no candidate pairs → no training even with candles present.
        SeedCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        var worker = CreateWorker(db, trainer.Object);

        await worker.RunCycleAsync(CancellationToken.None);
        trainer.Verify(t => t.TrainAsync(
            It.IsAny<string>(), It.IsAny<Timeframe>(), It.IsAny<IReadOnlyList<float[][]>>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_Trains_And_Promotes_Fresh_Encoder_For_Stale_Pair()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer
            .Setup(t => t.TrainAsync("EURUSD", Timeframe.H1,
                It.IsAny<IReadOnlyList<float[][]>>(), 16, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 1.5, TrainingSamples = 100,
                    EncoderBytes = [1, 2, 3],
                    TrainedAt = DateTime.UtcNow, IsActive = true
                });

        var worker = CreateWorker(db, trainer.Object);
        await worker.RunCycleAsync(CancellationToken.None);

        trainer.Verify(t => t.TrainAsync("EURUSD", Timeframe.H1,
            It.IsAny<IReadOnlyList<float[][]>>(), 16, 3, It.IsAny<CancellationToken>()), Times.Once);

        var encoders = await db.Set<MLCpcEncoder>().ToListAsync();
        var active = Assert.Single(encoders.Where(e => e.IsActive));
        Assert.Equal("EURUSD", active.Symbol);
        Assert.Equal(Timeframe.H1, active.Timeframe);

        var log = await db.Set<MLCpcEncoderTrainingLog>().SingleAsync();
        Assert.Equal("promoted", log.Outcome);
        Assert.Equal("accepted", log.Reason);
        Assert.True(log.TrainingSequences > 0);
        Assert.True(log.ValidationSequences >= 20);
        Assert.True(double.IsFinite(log.ValidationInfoNceLoss!.Value));
        Assert.Equal(1.5, log.TrainInfoNceLoss);
        Assert.Equal(log.ValidationInfoNceLoss.Value, active.InfoNceLoss);
    }

    [Fact]
    public async Task RunCycleAsync_Deactivates_Previous_Active_Before_Inserting_New_One()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        // Seed an older "active" encoder that is stale enough to rotate (older than RetrainIntervalHours=168h).
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 99, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            EmbeddingDim = 16, InfoNceLoss = 3.0,
            EncoderBytes = [9], TrainedAt = DateTime.UtcNow.AddDays(-14),
            IsActive = true
        });
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer
            .Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 1.0,  // easily beats prior 3.0 at 2% improvement threshold
                    EncoderBytes = [7],
                    TrainedAt = DateTime.UtcNow, IsActive = true
                });

        var worker = CreateWorker(db, trainer.Object);
        await worker.RunCycleAsync(CancellationToken.None);

        var rows = await db.Set<MLCpcEncoder>().OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.False(rows.Single(r => r.Id == 99).IsActive);
        Assert.True(rows.Single(r => r.Id != 99).IsActive);
    }

    [Fact]
    public async Task RunCycleAsync_Rejects_When_Loss_Does_Not_Beat_Prior_By_Min_Improvement()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            EmbeddingDim = 16, InfoNceLoss = 1.00,
            EncoderBytes = [1], TrainedAt = DateTime.UtcNow.AddDays(-10),
            IsActive = true
        });
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 0.995, // only 0.5% better — below 2% MinImprovement
                    EncoderBytes = [2], TrainedAt = DateTime.UtcNow, IsActive = true
                });

        var worker = CreateWorker(db, trainer.Object);
        await worker.RunCycleAsync(CancellationToken.None);

        var rows = await db.Set<MLCpcEncoder>().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(1, rows[0].Id); // still the original
    }

    [Fact]
    public async Task RunCycleAsync_Skips_When_Candles_Below_MinCandles()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 100); // < default MinCandles=1000
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        var worker = CreateWorker(db, trainer.Object);

        await worker.RunCycleAsync(CancellationToken.None);
        trainer.Verify(t => t.TrainAsync(
            It.IsAny<string>(), It.IsAny<Timeframe>(), It.IsAny<IReadOnlyList<float[][]>>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_Raises_Alert_After_Consecutive_Failures()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        AddConfig(db, "MLCpc:ConsecutiveFailAlertThreshold", "2", ConfigDataType.Int);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = double.NaN, // NaN → rejected by loss-out-of-bounds gate
                    EncoderBytes = [1], TrainedAt = DateTime.UtcNow, IsActive = true
                });

        var worker = CreateWorker(db, trainer.Object, options: new MLCpcOptions
        {
            ConsecutiveFailAlertThreshold = 99
        });

        await worker.RunCycleAsync(CancellationToken.None); // fail 1
        await worker.RunCycleAsync(CancellationToken.None); // fail 2 → alert

        var alerts = await db.Set<Alert>()
            .Where(a => a.AlertType == AlertType.DataQualityIssue)
            .ToListAsync();
        Assert.Single(alerts);
        Assert.Equal(AlertSeverity.Medium, alerts[0].Severity);
        Assert.Contains("MLCpcPretrainer", alerts[0].DeduplicationKey);
    }

    [Fact]
    public async Task RunCycleAsync_Does_Not_Duplicate_Active_Alert_After_Threshold()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        AddConfig(db, "MLCpc:ConsecutiveFailAlertThreshold", "2", ConfigDataType.Int);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = double.NaN,
                    EncoderBytes = [1], TrainedAt = DateTime.UtcNow, IsActive = true
                });

        var worker = CreateWorker(db, trainer.Object, options: new MLCpcOptions
        {
            ConsecutiveFailAlertThreshold = 99
        });

        await worker.RunCycleAsync(CancellationToken.None);
        await worker.RunCycleAsync(CancellationToken.None);
        await worker.RunCycleAsync(CancellationToken.None);

        var alerts = await db.Set<Alert>()
            .Where(a => a.AlertType == AlertType.DataQualityIssue)
            .ToListAsync();
        var alert = Assert.Single(alerts);
        Assert.Contains("\"ConsecutiveFailures\":3", alert.ConditionJson);
    }

    [Fact]
    public async Task RunCycleAsync_Per_Regime_Trains_Separate_Encoder_With_Regime_Tagged()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 2000);
        // Tag every candle's timestamp window as "Trending" so FilterCandlesByRegimeAsync
        // includes them all under that regime and excludes them from every other regime.
        db.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Regime = MarketRegime.Trending, Confidence = 0.9m,
            DetectedAt = DateTime.UtcNow.AddYears(-1)
        });
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 1.5, EncoderBytes = [1], TrainedAt = DateTime.UtcNow, IsActive = true
                });

        // Raise MaxPairsPerCycle so one cycle can cover both null-regime and Trending in a
        // single test rather than needing multiple cycles.
        var worker = CreateWorker(db, trainer.Object, options: new MLCpcOptions
        {
            EmbeddingDim = 16,
            MinCandles = 1000,
            MinCandlesPerRegime = 500,
            MaxPairsPerCycle = 10,
            TrainPerRegime = true,
            SequenceLength = 60,
            PollIntervalSeconds = 3600,
        });

        await worker.RunCycleAsync(CancellationToken.None);

        var rows = await db.Set<MLCpcEncoder>().Where(e => e.IsActive).ToListAsync();

        // null-regime got all candles → trained.
        Assert.Contains(rows, r => r.Regime == null);
        // Trending got all candles (the only regime snapshot) → trained.
        Assert.Contains(rows, r => r.Regime == MarketRegime.Trending);
        // Other regimes got zero candles after filtering → not trained.
        Assert.DoesNotContain(rows, r => r.Regime == MarketRegime.Ranging);
        Assert.DoesNotContain(rows, r => r.Regime == MarketRegime.Crisis);
    }

    [Fact]
    public async Task RunCycleAsync_Per_Regime_Rotation_Does_Not_Deactivate_Other_Regime_Rows()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 2000);
        db.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Regime = MarketRegime.Trending, Confidence = 0.9m,
            DetectedAt = DateTime.UtcNow.AddYears(-1)
        });
        // Pre-existing active encoders for Crisis and Ranging that should NOT be touched
        // when a Trending retrain rotates. Stale-enough to be candidates but they won't
        // be trained this cycle because the Trending snapshot owns all candles.
        db.Set<MLCpcEncoder>().AddRange(
            new MLCpcEncoder { Id = 50, Symbol = "EURUSD", Timeframe = Timeframe.H1, Regime = MarketRegime.Crisis, EmbeddingDim = 16, InfoNceLoss = 2.0, EncoderBytes = [5], TrainedAt = DateTime.UtcNow.AddDays(-30), IsActive = true },
            new MLCpcEncoder { Id = 60, Symbol = "EURUSD", Timeframe = Timeframe.H1, Regime = MarketRegime.Ranging, EmbeddingDim = 16, InfoNceLoss = 2.0, EncoderBytes = [6], TrainedAt = DateTime.UtcNow.AddDays(-30), IsActive = true }
        );
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 1.0, EncoderBytes = [9], TrainedAt = DateTime.UtcNow, IsActive = true
                });

        var worker = CreateWorker(db, trainer.Object, options: new MLCpcOptions
        {
            EmbeddingDim = 16,
            MinCandles = 1000,
            MinCandlesPerRegime = 500,
            MaxPairsPerCycle = 10,
            TrainPerRegime = true,
            SequenceLength = 60,
            PollIntervalSeconds = 3600,
        });

        await worker.RunCycleAsync(CancellationToken.None);

        // Crisis + Ranging rows untouched — their regimes had no candles in the snapshot
        // timeline, so training was skipped and the old rows remain active.
        var crisis = await db.Set<MLCpcEncoder>().SingleAsync(e => e.Id == 50);
        var ranging = await db.Set<MLCpcEncoder>().SingleAsync(e => e.Id == 60);
        Assert.True(crisis.IsActive);
        Assert.True(ranging.IsActive);
    }

    [Fact]
    public async Task RunCycleAsync_Picks_Pretrainer_By_Configured_EncoderType()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var linear = new Mock<ICpcPretrainer>();
        linear.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        var tcn = new Mock<ICpcPretrainer>();
        tcn.SetupGet(t => t.Kind).Returns(CpcEncoderType.Tcn);
        tcn.Setup(t => t.TrainAsync("EURUSD", Timeframe.H1,
                It.IsAny<IReadOnlyList<float[][]>>(), 16, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MLCpcEncoder
            {
                Symbol = "EURUSD", Timeframe = Timeframe.H1,
                EncoderType = CpcEncoderType.Tcn,
                EmbeddingDim = 16, PredictionSteps = 3,
                InfoNceLoss = 1.2, EncoderBytes = [1],
                TrainedAt = DateTime.UtcNow, IsActive = true
            });

        var worker = CreateWorker(db, trainers: [linear.Object, tcn.Object], options: new MLCpcOptions
        {
            EmbeddingDim = 16,
            MinCandles = 1000,
            SequenceLength = 60,
            PollIntervalSeconds = 3600,
            EncoderType = CpcEncoderType.Tcn,
        });

        await worker.RunCycleAsync(CancellationToken.None);

        // Only the TCN pretrainer should have been called.
        linear.Verify(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
            It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        tcn.Verify(t => t.TrainAsync("EURUSD", Timeframe.H1,
            It.IsAny<IReadOnlyList<float[][]>>(), 16, 3, It.IsAny<CancellationToken>()), Times.Once);

        var active = await db.Set<MLCpcEncoder>().SingleAsync(e => e.IsActive);
        Assert.Equal(CpcEncoderType.Tcn, active.EncoderType);
    }

    [Fact]
    public async Task RunCycleAsync_Configured_EncoderType_Change_Forces_Retrain_Even_When_Other_Type_Is_Fresh()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 10,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EncoderType = CpcEncoderType.Linear,
            EmbeddingDim = 16,
            InfoNceLoss = 1.0,
            EncoderBytes = [1],
            TrainedAt = DateTime.UtcNow,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var tcn = new Mock<ICpcPretrainer>();
        tcn.SetupGet(t => t.Kind).Returns(CpcEncoderType.Tcn);
        tcn.Setup(t => t.TrainAsync("EURUSD", Timeframe.H1,
                It.IsAny<IReadOnlyList<float[][]>>(), 16, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MLCpcEncoder
            {
                Symbol = "EURUSD", Timeframe = Timeframe.H1,
                EncoderType = CpcEncoderType.Tcn,
                EmbeddingDim = 16, PredictionSteps = 3,
                InfoNceLoss = 1.2, EncoderBytes = [2],
                TrainedAt = DateTime.UtcNow, IsActive = true
            });

        var worker = CreateWorker(db, trainers: [tcn.Object], options: new MLCpcOptions
        {
            EmbeddingDim = 16,
            MinCandles = 1000,
            SequenceLength = 60,
            PollIntervalSeconds = 3600,
            EncoderType = CpcEncoderType.Tcn,
        });

        await worker.RunCycleAsync(CancellationToken.None);

        tcn.Verify(t => t.TrainAsync("EURUSD", Timeframe.H1,
            It.IsAny<IReadOnlyList<float[][]>>(), 16, 3, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False((await db.Set<MLCpcEncoder>().SingleAsync(e => e.Id == 10)).IsActive);
        Assert.True(await db.Set<MLCpcEncoder>().AnyAsync(e => e.EncoderType == CpcEncoderType.Tcn && e.IsActive));
    }

    [Fact]
    public async Task RunCycleAsync_Rejects_When_Projection_Smoke_Test_Fails()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 1.0,
                    EncoderBytes = [1],
                    TrainedAt = DateTime.UtcNow,
                    IsActive = true
                });

        var projection = new Mock<ICpcEncoderProjection>();
        projection.Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns([float.NaN]);

        var worker = CreateWorker(db, trainer.Object, projection: projection.Object);

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLCpcEncoder>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Rejects_When_Holdout_Validation_Loss_Is_Invalid()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 1.0,
                    EncoderBytes = [1],
                    TrainedAt = DateTime.UtcNow,
                    IsActive = true
                });

        var projection = new Mock<ICpcEncoderProjection>();
        projection.Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] _) => new float[e.EmbeddingDim]);
        projection.Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] seq) =>
                seq.Select(_ =>
                {
                    var row = new float[e.EmbeddingDim];
                    row[0] = float.NaN;
                    return row;
                }).ToArray());

        var worker = CreateWorker(db, trainer.Object, projection: projection.Object);

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLCpcEncoder>().ToListAsync());
        var log = await db.Set<MLCpcEncoderTrainingLog>().SingleAsync();
        Assert.Equal("rejected", log.Outcome);
        Assert.Equal("validation_loss_out_of_bounds", log.Reason);
    }

    [Fact]
    public async Task RunCycleAsync_Rejects_When_Holdout_Embedding_Is_Collapsed()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 1.0,
                    EncoderBytes = [1],
                    TrainedAt = DateTime.UtcNow,
                    IsActive = true
                });

        var projection = new Mock<ICpcEncoderProjection>();
        projection.Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] _) => new float[e.EmbeddingDim]);
        projection.Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] seq) =>
                seq.Select(_ => new float[e.EmbeddingDim]).ToArray());

        var worker = CreateWorker(db, trainer.Object, projection: projection.Object);

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLCpcEncoder>().ToListAsync());
        var log = await db.Set<MLCpcEncoderTrainingLog>().SingleAsync();
        Assert.Equal("rejected", log.Outcome);
        Assert.Equal("embedding_collapsed", log.Reason);
        Assert.Contains("ValidationMeanEmbeddingL2Norm", log.DiagnosticsJson);
        Assert.Contains("ValidationMeanEmbeddingVariance", log.DiagnosticsJson);
    }

    [Fact]
    public async Task RunCycleAsync_Uses_Holdout_Validation_Loss_When_Checking_Improvement()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EmbeddingDim = 16,
            InfoNceLoss = 2.0,
            EncoderBytes = [1],
            TrainedAt = DateTime.UtcNow.AddDays(-10),
            IsActive = true
        });
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 0.5,
                    EncoderBytes = [2],
                    TrainedAt = DateTime.UtcNow,
                    IsActive = true
                });

        var projection = CreateNearRandomProjection();
        var worker = CreateWorker(db, trainer.Object, projection: projection);

        await worker.RunCycleAsync(CancellationToken.None);

        var rows = await db.Set<MLCpcEncoder>().ToListAsync();
        var active = Assert.Single(rows);
        Assert.Equal(1, active.Id);

        var log = await db.Set<MLCpcEncoderTrainingLog>().SingleAsync();
        Assert.Equal("rejected", log.Outcome);
        Assert.Equal("no_improvement", log.Reason);
        Assert.Equal(0.5, log.TrainInfoNceLoss);
        Assert.True(log.ValidationInfoNceLoss > 2.0);
    }

    [Fact]
    public async Task RunCycleAsync_Rejects_When_Downstream_Probe_Has_No_Directional_Lift()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLCpc:EnableDownstreamProbeGate", "true", ConfigDataType.Bool);
        AddConfig(db, "MLCpc:MinDownstreamProbeSamples", "10", ConfigDataType.Int);
        AddConfig(db, "MLCpc:MinDownstreamProbeBalancedAccuracy", "0.80", ConfigDataType.Decimal);
        AddConfig(db, "MLCpc:MaxValidationLoss", "1000", ConfigDataType.Decimal);
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 1.0,
                    EncoderBytes = [2],
                    TrainedAt = DateTime.UtcNow,
                    IsActive = true
                });

        var worker = CreateWorker(db, trainer.Object, projection: CreateNearRandomProjection());

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLCpcEncoder>().ToListAsync());
        var log = await db.Set<MLCpcEncoderTrainingLog>().SingleAsync();
        Assert.Equal("rejected", log.Outcome);
        Assert.Equal("downstream_probe_below_floor", log.Reason);
        Assert.Contains("DownstreamProbeCandidateBalancedAccuracy", log.DiagnosticsJson);
    }

    [Fact]
    public async Task RunCycleAsync_Rejects_When_Downstream_Probe_Does_Not_Beat_Prior()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLCpc:EnableDownstreamProbeGate", "true", ConfigDataType.Bool);
        AddConfig(db, "MLCpc:MinDownstreamProbeSamples", "10", ConfigDataType.Int);
        AddConfig(db, "MLCpc:MinDownstreamProbeBalancedAccuracy", "0.50", ConfigDataType.Decimal);
        AddConfig(db, "MLCpc:MinDownstreamProbeImprovement", "0.01", ConfigDataType.Decimal);
        AddConfig(db, "MLCpc:MaxValidationLoss", "1000", ConfigDataType.Decimal);
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EmbeddingDim = 16,
            PredictionSteps = 3,
            InfoNceLoss = 4.0,
            EncoderBytes = [1],
            TrainedAt = DateTime.UtcNow.AddDays(-10),
            IsActive = true
        });
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 1.0,
                    EncoderBytes = [1],
                    TrainedAt = DateTime.UtcNow,
                    IsActive = true
                });

        var worker = CreateWorker(db, trainer.Object, projection: CreateFutureAwareProjection());

        await worker.RunCycleAsync(CancellationToken.None);

        var active = await db.Set<MLCpcEncoder>().SingleAsync(e => e.IsActive);
        Assert.Equal(1, active.Id);

        var log = await db.Set<MLCpcEncoderTrainingLog>().SingleAsync();
        Assert.Equal("rejected", log.Outcome);
        Assert.Equal("downstream_probe_no_lift", log.Reason);
        Assert.Contains("DownstreamProbePriorBalancedAccuracy", log.DiagnosticsJson);
    }

    [Fact]
    public async Task RunCycleAsync_Raises_Stale_Encoder_Alert_When_Active_Encoder_Exceeds_Slo()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLCpc:StaleEncoderAlertHours", "240", ConfigDataType.Int);
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 100); // skip training; alert should still be raised.
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EmbeddingDim = 16,
            InfoNceLoss = 1.0,
            EncoderBytes = [1],
            TrainedAt = DateTime.UtcNow.AddDays(-20),
            IsActive = true
        });
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        var worker = CreateWorker(db, trainer.Object);

        await worker.RunCycleAsync(CancellationToken.None);

        var alert = await db.Set<Alert>().SingleAsync(a =>
            a.AlertType == AlertType.DataQualityIssue &&
            a.DeduplicationKey.Contains("StaleEncoder"));
        Assert.Contains("StaleEncoderAlertHours", alert.ConditionJson);
    }

    [Fact]
    public async Task RunCycleAsync_Skips_When_No_Pretrainer_Matches_Configured_EncoderType()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        // Only the Linear pretrainer is registered but config requests Tcn → skip, no promote.
        var linear = new Mock<ICpcPretrainer>();
        linear.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);

        var worker = CreateWorker(db, trainers: [linear.Object], options: new MLCpcOptions
        {
            EmbeddingDim = 16,
            MinCandles = 1000,
            SequenceLength = 60,
            PollIntervalSeconds = 3600,
            EncoderType = CpcEncoderType.Tcn,
        });

        await worker.RunCycleAsync(CancellationToken.None);

        linear.Verify(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
            It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Empty(await db.Set<MLCpcEncoder>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Continues_With_Next_Candidate_When_Pretrainer_Throws()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        SeedModelAndCandles(db, "GBPUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync("EURUSD", Timeframe.H1,
                It.IsAny<IReadOnlyList<float[][]>>(), 16, 3, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        trainer.Setup(t => t.TrainAsync("GBPUSD", Timeframe.H1,
                It.IsAny<IReadOnlyList<float[][]>>(), 16, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MLCpcEncoder
            {
                Symbol = "GBPUSD", Timeframe = Timeframe.H1,
                EmbeddingDim = 16, PredictionSteps = 3,
                InfoNceLoss = 1.0, EncoderBytes = [1],
                TrainedAt = DateTime.UtcNow, IsActive = true
            });

        var worker = CreateWorker(db, trainer.Object, options: new MLCpcOptions
        {
            EmbeddingDim = 16,
            MinCandles = 1000,
            SequenceLength = 60,
            PollIntervalSeconds = 3600,
            MaxPairsPerCycle = 10,
        });

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.True(await db.Set<MLCpcEncoder>().AnyAsync(e => e.Symbol == "GBPUSD" && e.IsActive));
        Assert.True(await db.Set<MLCpcEncoderTrainingLog>().AnyAsync(l =>
            l.Symbol == "EURUSD" && l.Outcome == "rejected" && l.Reason == "trainer_exception"));
        Assert.True(await db.Set<MLCpcEncoderTrainingLog>().AnyAsync(l =>
            l.Symbol == "GBPUSD" && l.Outcome == "promoted"));
    }

    [Fact]
    public async Task RunCycleAsync_Does_Not_Replace_Newer_Better_Active_Encoder_Promoted_During_Training()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EncoderType = CpcEncoderType.Linear,
            EmbeddingDim = 16,
            InfoNceLoss = 3.0,
            EncoderBytes = [1],
            TrainedAt = DateTime.UtcNow.AddDays(-30),
            IsActive = true
        });
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync("EURUSD", Timeframe.H1,
                It.IsAny<IReadOnlyList<float[][]>>(), 16, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var old = db.Set<MLCpcEncoder>().Single(e => e.Id == 1);
                old.IsActive = false;
                db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
                {
                    Id = 2,
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    EncoderType = CpcEncoderType.Linear,
                    EmbeddingDim = 16,
                    InfoNceLoss = 1.0,
                    EncoderBytes = [2],
                    TrainedAt = DateTime.UtcNow,
                    IsActive = true
                });
                db.SaveChanges();

                return new MLCpcEncoder
                {
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    EncoderType = CpcEncoderType.Linear,
                    EmbeddingDim = 16,
                    PredictionSteps = 3,
                    InfoNceLoss = 1.6,
                    EncoderBytes = [3],
                    TrainedAt = DateTime.UtcNow,
                    IsActive = true
                };
            });

        var worker = CreateWorker(db, trainer.Object);
        await worker.RunCycleAsync(CancellationToken.None);

        var active = await db.Set<MLCpcEncoder>().SingleAsync(e => e.IsActive);
        Assert.Equal(2, active.Id);
        Assert.Equal(1.0, active.InfoNceLoss);
        Assert.False(await db.Set<MLCpcEncoder>().AnyAsync(e => e.EncoderBytes != null && e.EncoderBytes.SequenceEqual(new byte[] { 3 })));

        var log = await db.Set<MLCpcEncoderTrainingLog>().SingleAsync();
        Assert.Equal("skipped", log.Outcome);
        Assert.Equal("superseded_by_better_active", log.Reason);
    }

    [Fact]
    public async Task RunCycleAsync_Per_Regime_Loads_Expanded_Window_For_Rare_Older_Regime()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 2000);
        await db.SaveChangesAsync();
        var firstCandle = await db.Set<Candle>()
            .Where(c => c.Symbol == "EURUSD" && c.Timeframe == Timeframe.H1)
            .OrderBy(c => c.Timestamp)
            .FirstAsync();
        var recentBoundary = DateTime.UtcNow.AddHours(-700);
        db.Set<MarketRegimeSnapshot>().AddRange(
            new MarketRegimeSnapshot
            {
                Symbol = "EURUSD", Timeframe = Timeframe.H1,
                Regime = MarketRegime.Trending, Confidence = 0.9m,
                DetectedAt = firstCandle.Timestamp.AddHours(-1)
            },
            new MarketRegimeSnapshot
            {
                Symbol = "EURUSD", Timeframe = Timeframe.H1,
                Regime = MarketRegime.Ranging, Confidence = 0.9m,
                DetectedAt = recentBoundary
            });
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        trainer.Setup(t => t.TrainAsync(It.IsAny<string>(), It.IsAny<Timeframe>(),
                It.IsAny<IReadOnlyList<float[][]>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, Timeframe tf, IReadOnlyList<float[][]> _, int embed, int steps, CancellationToken _ct) =>
                new MLCpcEncoder
                {
                    Symbol = s, Timeframe = tf,
                    EmbeddingDim = embed, PredictionSteps = steps,
                    InfoNceLoss = 1.0, EncoderBytes = [1],
                    TrainedAt = DateTime.UtcNow, IsActive = true
                });

        var worker = CreateWorker(db, trainer.Object, options: new MLCpcOptions
        {
            EmbeddingDim = 16,
            MinCandles = 1000,
            MinCandlesPerRegime = 500,
            TrainingCandles = 1000,
            SequenceLength = 60,
            PollIntervalSeconds = 3600,
            MaxPairsPerCycle = 10,
            TrainPerRegime = true,
        });

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.True(await db.Set<MLCpcEncoder>().AnyAsync(e =>
            e.Symbol == "EURUSD" && e.Timeframe == Timeframe.H1 &&
            e.Regime == MarketRegime.Trending && e.IsActive));
        Assert.True(await db.Set<MLCpcEncoderTrainingLog>().AnyAsync(l =>
            l.Regime == MarketRegime.Trending && l.CandlesLoaded > 1000 && l.Outcome == "promoted"));
    }

    [Fact]
    public async Task RunCycleAsync_Skips_Candidate_When_Distributed_Lock_Is_Busy()
    {
        await using var db = CreateDbContext();
        SeedModelAndCandles(db, "EURUSD", Timeframe.H1, candleCount: 1500);
        await db.SaveChangesAsync();

        var trainer = new Mock<ICpcPretrainer>();
        trainer.SetupGet(t => t.Kind).Returns(CpcEncoderType.Linear);
        var distributedLock = new BusyDistributedLock();

        var worker = CreateWorker(
            db,
            trainer.Object,
            options: new MLCpcOptions
            {
                EmbeddingDim = 16,
                MinCandles = 1000,
                SequenceLength = 60,
                PollIntervalSeconds = 3600,
                LockTimeoutSeconds = 0,
            },
            distributedLock: distributedLock);

        await worker.RunCycleAsync(CancellationToken.None);

        trainer.Verify(t => t.TrainAsync(
            It.IsAny<string>(), It.IsAny<Timeframe>(), It.IsAny<IReadOnlyList<float[][]>>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var log = await db.Set<MLCpcEncoderTrainingLog>().SingleAsync();
        Assert.Equal("skipped", log.Outcome);
        Assert.Equal("lock_busy", log.Reason);
        Assert.Contains("LockKey", log.DiagnosticsJson);
        Assert.Contains("LockTimeoutSeconds", log.DiagnosticsJson);
        Assert.Single(distributedLock.RequestedKeys);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static CpcPretrainerWorker CreateWorker(
        DbContext db,
        ICpcPretrainer trainer,
        MLCpcOptions? options = null,
        ICpcEncoderProjection? projection = null,
        IDistributedLock? distributedLock = null)
        => CreateWorker(db, trainers: new[] { trainer }, options, projection, distributedLock);

    private static CpcPretrainerWorker CreateWorker(
        DbContext db,
        IReadOnlyList<ICpcPretrainer> trainers,
        MLCpcOptions? options = null,
        ICpcEncoderProjection? projection = null,
        IDistributedLock? distributedLock = null)
    {
        options ??= new MLCpcOptions
        {
            EmbeddingDim = 16,
            MinCandles = 1000,
            SequenceLength = 60,
            PollIntervalSeconds = 3600,
            EnableDownstreamProbeGate = false,
        };
        options.EnableDownstreamProbeGate = false;

        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);
        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        services.AddScoped(_ => writeContext.Object);
        foreach (var t in trainers)
            services.AddScoped(_ => t);
        if (projection is null)
            projection = CreateDataDependentProjection();
        services.AddScoped(_ => projection);
        var provider = services.BuildServiceProvider();

        return new CpcPretrainerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<CpcPretrainerWorker>>(),
            distributedLock ?? new NoopDistributedLock(),
            TimeProvider.System,
            null, // health monitor
            null, // metrics
            options,
            new MLCpcConfigReader(options));
    }

    private static void SeedModelAndCandles(DbContext db, string symbol, Timeframe timeframe, int candleCount)
    {
        var nextModelId = db.Set<MLModel>().Local
            .Select(m => m.Id)
            .DefaultIfEmpty(0)
            .Max() + 1;
        db.Set<MLModel>().Add(new MLModel
        {
            Id = nextModelId,
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = "1.0.0",
            FilePath = "/tmp/m.json",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainingSamples = 100,
            TrainedAt = DateTime.UtcNow.AddDays(-1),
            ActivatedAt = DateTime.UtcNow.AddDays(-1),
        });
        SeedCandles(db, symbol, timeframe, candleCount);
    }

    private static void SeedCandles(DbContext db, string symbol, Timeframe timeframe, int candleCount)
    {
        var rng = new Random(17);
        decimal price = 1.10m;
        var start = DateTime.UtcNow.AddHours(-candleCount);
        var nextCandleId = db.Set<Candle>().Local
            .Select(c => c.Id)
            .DefaultIfEmpty(0)
            .Max() + 1;
        for (int i = 0; i < candleCount; i++)
        {
            decimal d = (decimal)((rng.NextDouble() - 0.5) * 0.002);
            decimal open = price;
            decimal close = price + d;
            decimal hi = Math.Max(open, close) + 0.0001m;
            decimal lo = Math.Min(open, close) - 0.0001m;
            db.Set<Candle>().Add(new Candle
            {
                Id = nextCandleId + i,
                Symbol = symbol,
                Timeframe = timeframe,
                Timestamp = start.AddHours(i),
                Open = open, High = hi, Low = lo, Close = close,
                Volume = 1000m + i,
                IsClosed = true
            });
            price = close;
        }
    }

    private static void AddConfig(DbContext db, string key, string value, ConfigDataType dataType)
        => db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key, Value = value, DataType = dataType,
            IsHotReloadable = true, LastUpdatedAt = DateTime.UtcNow
        });

    private static ICpcEncoderProjection CreateDataDependentProjection()
    {
        var projectionMock = new Mock<ICpcEncoderProjection>();
        projectionMock
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] seq) => ProjectRow(e, seq[^1], seq.Length));
        projectionMock
            .Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] seq) =>
                seq.Select((row, i) => ProjectRow(e, row, i + 1)).ToArray());
        return projectionMock.Object;
    }

    private static ICpcEncoderProjection CreateNearRandomProjection()
    {
        var projectionMock = new Mock<ICpcEncoderProjection>();
        projectionMock
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] _) =>
            {
                var row = new float[e.EmbeddingDim];
                row[0] = 0.001f;
                return row;
            });
        projectionMock
            .Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] seq) =>
                seq.Select((_, i) =>
                {
                    var row = new float[e.EmbeddingDim];
                    row[0] = i % 2 == 0 ? 0.001f : -0.001f;
                    return row;
                }).ToArray());
        return projectionMock.Object;
    }

    private static ICpcEncoderProjection CreateFutureAwareProjection()
    {
        var projectionMock = new Mock<ICpcEncoderProjection>();
        projectionMock
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] seq) => ProjectFutureAware(e, seq)[^1]);
        projectionMock
            .Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] seq) => ProjectFutureAware(e, seq));
        return projectionMock.Object;
    }

    private static float[][] ProjectFutureAware(MLCpcEncoder encoder, float[][] sequence)
    {
        var result = new float[sequence.Length][];
        var polarity = encoder.EncoderBytes is { Length: > 0 } && encoder.EncoderBytes[0] == 2
            ? -1f
            : 1f;

        for (int i = 0; i < sequence.Length; i++)
        {
            double futureReturn = 0.0;
            for (int k = 1; k <= Math.Max(1, encoder.PredictionSteps) && i + k < sequence.Length; k++)
                futureReturn += sequence[i + k].Length > 3 ? sequence[i + k][3] : 0.0;

            var row = new float[encoder.EmbeddingDim];
            var signed = futureReturn >= 0.0 ? polarity : -polarity;
            for (int j = 0; j < row.Length; j++)
                row[j] = signed * (1.0f + (j * 0.01f));
            result[i] = row;
        }

        return result;
    }

    private static float[] ProjectRow(MLCpcEncoder encoder, float[] row, int ordinal)
    {
        var result = new float[encoder.EmbeddingDim];
        for (int i = 0; i < result.Length; i++)
        {
            var source = row.Length == 0 ? 0f : row[i % row.Length];
            result[i] = (float)((source * 0.001 * (i + 1)) + (ordinal * 0.0001));
        }

        return result;
    }

    private sealed class BusyDistributedLock : IDistributedLock
    {
        public List<string> RequestedKeys { get; } = [];

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
        {
            RequestedKeys.Add(lockKey);
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
        {
            RequestedKeys.Add(lockKey);
            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }

    private sealed class NoopDistributedLock : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(new Handle());

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(new Handle());

        private sealed class Handle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
