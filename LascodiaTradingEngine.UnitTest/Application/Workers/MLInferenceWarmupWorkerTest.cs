using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLInferenceWarmupWorkerTest
{
    [Fact]
    public async Task WarmupAsync_WarmsOnlyRoutableSnapshotBackedModelsAndUsesLatestRegime()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var active = AddModel(db, " eurusd ", Timeframe.H1, activatedAt: now.AddMinutes(-1));
        var fallback = AddModel(
            db,
            "GBPUSD",
            Timeframe.M15,
            status: MLModelStatus.Superseded,
            isFallbackChampion: true,
            activatedAt: now.AddMinutes(-2));
        AddModel(db, "USDJPY", Timeframe.H1, hasSnapshot: false, activatedAt: now);
        AddModel(db, "   ", Timeframe.H1, activatedAt: now.AddMinutes(-3));
        AddModel(db, "AUDUSD", Timeframe.H1, isSuppressed: true, activatedAt: now.AddMinutes(-4));
        AddModel(db, "NZDUSD", Timeframe.H1, isMetaLearner: true, activatedAt: now.AddMinutes(-5));
        AddModel(db, "USDCAD", Timeframe.H1, isMamlInitializer: true, activatedAt: now.AddMinutes(-6));
        AddModel(db, "EURJPY", Timeframe.H1, status: MLModelStatus.Training, activatedAt: now.AddMinutes(-7));

        AddCandles(db, "EURUSD", Timeframe.H1);
        AddCandles(db, "GBPUSD", Timeframe.M15);
        AddRegime(db, "EURUSD", Timeframe.H1, MarketRegime.Ranging, now.AddMinutes(-10));
        AddRegime(db, "EURUSD", Timeframe.H1, MarketRegime.Trending, now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var scorer = new FakeWarmupScorer();
        var worker = CreateWorker(db, scorer);

        var stats = await worker.WarmupAsync(CancellationToken.None);

        Assert.Equal(2, stats.Warmed);
        Assert.Equal(1, stats.SkippedEmptySnapshot);
        Assert.Equal(1, stats.SkippedInvalidModel);
        Assert.Equal(0, stats.Failed);
        Assert.Equal(0, stats.TimedOut);
        Assert.Equal([active.Id, fallback.Id], scorer.Calls.Select(c => c.ModelId).Order().ToArray());
        Assert.Contains(scorer.Calls, c =>
            c.ModelId == active.Id &&
            c.Symbol == "EURUSD" &&
            c.CurrentRegime == nameof(MarketRegime.Trending) &&
            c.CandleCount == MLFeatureHelper.LookbackWindow + 2);
    }

    [Fact]
    public async Task WarmupAsync_AppliesModelCapAfterMissingAndEmptySnapshots()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        AddConfig(db, "MLWarmup:MaxModelsPerStartup", "1", now.AddMinutes(-1));
        AddModel(db, "USDJPY", Timeframe.H1, hasSnapshot: false, activatedAt: now);
        AddModel(db, "AUDUSD", Timeframe.H1, emptySnapshot: true, activatedAt: now.AddSeconds(-1));
        var valid = AddModel(db, "EURUSD", Timeframe.H1, activatedAt: now.AddSeconds(-2));
        AddCandles(db, "EURUSD", Timeframe.H1);
        await db.SaveChangesAsync();

        var scorer = new FakeWarmupScorer();
        var worker = CreateWorker(db, scorer);

        var stats = await worker.WarmupAsync(CancellationToken.None);

        Assert.Equal(1, stats.Warmed);
        Assert.Equal(2, stats.SkippedEmptySnapshot);
        Assert.Equal(0, stats.SkippedLimit);
        Assert.Equal(valid.Id, Assert.Single(scorer.Calls).ModelId);
    }

    [Fact]
    public async Task WarmupAsync_UsesLatestCaseInsensitiveRuntimeDisableBeforeResolvingScorer()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        AddConfig(db, "MLWarmup:InferenceWarmupEnabled", "true", now.AddMinutes(-2));
        AddConfig(db, "mlwarmup:inferencewarmupenabled", "false", now.AddMinutes(-1));
        AddModel(db, "EURUSD", Timeframe.H1, activatedAt: now);
        AddCandles(db, "EURUSD", Timeframe.H1);
        await db.SaveChangesAsync();

        var scorer = new FakeWarmupScorer();
        var worker = CreateWorker(db, scorer);

        var stats = await worker.WarmupAsync(CancellationToken.None);

        Assert.Equal(0, stats.Warmed);
        Assert.Equal(0, scorer.Attempts);
        Assert.Empty(scorer.Calls);
    }

    [Fact]
    public async Task WarmupAsync_AbortsAfterConfiguredTimeoutThreshold()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        AddConfig(db, "MLWarmup:ModelTimeoutSeconds", "1", now.AddMinutes(-2));
        AddConfig(db, "MLWarmup:MaxTimeoutsBeforeAbort", "1", now.AddMinutes(-1));
        AddModel(db, "EURUSD", Timeframe.H1, activatedAt: now);
        AddModel(db, "GBPUSD", Timeframe.H1, activatedAt: now.AddSeconds(-1));
        AddCandles(db, "EURUSD", Timeframe.H1);
        AddCandles(db, "GBPUSD", Timeframe.H1);
        await db.SaveChangesAsync();

        var scorer = new FakeWarmupScorer { Delay = TimeSpan.FromSeconds(5) };
        var worker = CreateWorker(db, scorer);

        var stats = await worker.WarmupAsync(CancellationToken.None);

        Assert.Equal(0, stats.Warmed);
        Assert.Equal(1, stats.TimedOut);
        Assert.Equal(0, stats.Failed);
        Assert.Equal(1, scorer.Attempts);
        Assert.Empty(scorer.Calls);
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLInferenceWarmupWorker CreateWorker(
        WriteApplicationDbContext db,
        FakeWarmupScorer scorer,
        IConfiguration? configuration = null,
        MLInferenceWarmupOptions? options = null)
    {
        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        services.AddScoped<IMLModelWarmupScorer>(_ => scorer);
        var provider = services.BuildServiceProvider();

        return new MLInferenceWarmupWorker(
            NullLogger<MLInferenceWarmupWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration ?? new ConfigurationBuilder().Build(),
            options: options ?? new MLInferenceWarmupOptions());
    }

    private static MLModel AddModel(
        WriteApplicationDbContext db,
        string symbol,
        Timeframe timeframe,
        MLModelStatus status = MLModelStatus.Active,
        bool isActive = true,
        bool hasSnapshot = true,
        bool emptySnapshot = false,
        bool isFallbackChampion = false,
        bool isSuppressed = false,
        bool isMetaLearner = false,
        bool isMamlInitializer = false,
        DateTime? activatedAt = null)
    {
        var model = new MLModel
        {
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = Guid.NewGuid().ToString("N"),
            FilePath = "memory",
            Status = status,
            IsActive = isActive,
            TrainingSamples = 100,
            TrainedAt = activatedAt?.AddHours(-1) ?? DateTime.UtcNow.AddHours(-1),
            ActivatedAt = activatedAt,
            ModelBytes = hasSnapshot
                ? emptySnapshot ? Array.Empty<byte>() : new byte[] { 1, 2, 3 }
                : null,
            IsFallbackChampion = isFallbackChampion,
            IsSuppressed = isSuppressed,
            IsMetaLearner = isMetaLearner,
            IsMamlInitializer = isMamlInitializer
        };

        db.Set<MLModel>().Add(model);
        return model;
    }

    private static void AddCandles(
        WriteApplicationDbContext db,
        string symbol,
        Timeframe timeframe,
        int count = MLFeatureHelper.LookbackWindow + 2)
    {
        var start = DateTime.UtcNow.AddMinutes(-count);
        for (int i = 0; i < count; i++)
        {
            db.Set<Candle>().Add(new Candle
            {
                Symbol = symbol,
                Timeframe = timeframe,
                Timestamp = start.AddMinutes(i),
                Open = 1.1000m + i * 0.0001m,
                High = 1.1010m + i * 0.0001m,
                Low = 1.0990m + i * 0.0001m,
                Close = 1.1005m + i * 0.0001m,
                Volume = 100 + i,
                IsClosed = true
            });
        }
    }

    private static void AddRegime(
        WriteApplicationDbContext db,
        string symbol,
        Timeframe timeframe,
        MarketRegime regime,
        DateTime detectedAt)
        => db.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Symbol = symbol,
            Timeframe = timeframe,
            Regime = regime,
            Confidence = 0.9m,
            ADX = 30m,
            ATR = 0.0015m,
            BollingerBandWidth = 0.0020m,
            DetectedAt = detectedAt
        });

    private static void AddConfig(
        WriteApplicationDbContext db,
        string key,
        string value,
        DateTime lastUpdatedAt)
        => db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = ConfigDataType.String,
            IsHotReloadable = true,
            LastUpdatedAt = lastUpdatedAt
        });

    private sealed class FakeWarmupScorer : IMLModelWarmupScorer
    {
        public List<WarmupCall> Calls { get; } = [];
        public TimeSpan Delay { get; init; }
        public int Attempts { get; private set; }

        public async Task WarmupModelAsync(
            MLModel model,
            IReadOnlyList<Candle> candles,
            string? currentRegime,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            Calls.Add(new WarmupCall(model.Id, model.Symbol, model.Timeframe, currentRegime, candles.Count));
        }
    }

    private sealed record WarmupCall(
        long ModelId,
        string Symbol,
        Timeframe Timeframe,
        string? CurrentRegime,
        int CandleCount);
}
