using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// Smoke test for the full <see cref="CpcPretrainerWorker"/> lifecycle on real Postgres.
/// Applies every migration (including <c>AddCpcEncoder</c>, <c>AddCpcEncoderRegime</c>, and
/// <c>AddCpcEncoderType</c>), seeds a supervised model + candles, runs one worker cycle with
/// the real <see cref="CpcPretrainer"/> (no mocks), then asserts an active <see cref="MLCpcEncoder"/>
/// lands with finite loss, non-empty weights, and a stamped <see cref="CpcEncoderType"/>.
///
/// <para>
/// This is the check the unit tests can't make: that the migrations apply cleanly, the EF
/// config maps the new columns to Postgres types correctly, and the write transaction over
/// real SQL behaves the way the in-memory provider suggested.
/// </para>
/// </summary>
public class CpcPretrainerWorkerPostgresIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public CpcPretrainerWorkerPostgresIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunCycleAsync_Trains_And_Promotes_Linear_Encoder_On_Postgres()
    {
        await EnsureMigratedAsync();
        await SeedModelAndCandlesAsync("EURUSD", Timeframe.H1, candleCount: 1500);

        var worker = CreateWorker(new MLCpcOptions
        {
            EmbeddingDim = 16,
            MinCandles = 1000,
            SequenceLength = 60,
            SequenceStride = 32,
            MaxSequences = 200,
            PollIntervalSeconds = 3600,
            EncoderType = CpcEncoderType.Linear,
        });

        await worker.RunCycleAsync(CancellationToken.None);

        await using var assertContext = CreateContext();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", assertContext.Database.ProviderName);

        var encoder = await assertContext.Set<MLCpcEncoder>()
            .Where(e => e.Symbol == "EURUSD" && e.Timeframe == Timeframe.H1 && e.IsActive)
            .SingleAsync();

        Assert.Equal(CpcEncoderType.Linear, encoder.EncoderType);
        Assert.Equal(16, encoder.EmbeddingDim);
        Assert.True(double.IsFinite(encoder.InfoNceLoss),
            $"InfoNceLoss should be finite after Postgres cycle, got {encoder.InfoNceLoss}.");
        Assert.NotNull(encoder.EncoderBytes);
        Assert.NotEmpty(encoder.EncoderBytes);
        Assert.Null(encoder.Regime); // global encoder
    }

    [Fact]
    public async Task RunCycleAsync_Trains_And_Promotes_Tcn_Encoder_On_Postgres()
    {
        await EnsureMigratedAsync();
        await SeedModelAndCandlesAsync("GBPUSD", Timeframe.H1, candleCount: 1500);

        var worker = CreateWorker(new MLCpcOptions
        {
            EmbeddingDim = 16,
            MinCandles = 1000,
            SequenceLength = 60,
            SequenceStride = 32,
            MaxSequences = 150,
            PollIntervalSeconds = 3600,
            EncoderType = CpcEncoderType.Tcn,
        });

        await worker.RunCycleAsync(CancellationToken.None);

        await using var assertContext = CreateContext();
        var encoder = await assertContext.Set<MLCpcEncoder>()
            .Where(e => e.Symbol == "GBPUSD" && e.Timeframe == Timeframe.H1 && e.IsActive)
            .SingleAsync();

        Assert.Equal(CpcEncoderType.Tcn, encoder.EncoderType);
        Assert.NotNull(encoder.EncoderBytes);
        Assert.NotEmpty(encoder.EncoderBytes);

        // Round-trip through the projection service — end-to-end inference sanity check on Postgres-persisted bytes.
        var projection = new CpcEncoderProjection();
        var syntheticSeq = Enumerable.Range(0, 30)
            .Select(_ => new[] { 0.01f, 0.02f, -0.01f, 0.005f, 0.0f, 0.5f })
            .ToArray();
        var embedding = projection.ProjectLatest(encoder, syntheticSeq);
        Assert.Equal(encoder.EmbeddingDim, embedding.Length);
        foreach (var v in embedding) Assert.True(float.IsFinite(v));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private CpcPretrainerWorker CreateWorker(MLCpcOptions options)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new DbContextAccessor(CreateContext()));
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        // Register both real pretrainers; the worker selects by Kind.
        services.AddScoped<ICpcPretrainer, CpcPretrainer>();
        services.AddScoped<ICpcPretrainer, CpcTcnPretrainer>();
        services.AddScoped<ICpcEncoderProjection, CpcEncoderProjection>();
        var provider = services.BuildServiceProvider();

        return new CpcPretrainerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CpcPretrainerWorker>.Instance,
            TimeProvider.System,
            healthMonitor: null,
            metrics: null,
            options,
            new MLCpcConfigReader(options));
    }

    private WriteApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task EnsureMigratedAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
    }

    private async Task SeedModelAndCandlesAsync(string symbol, Timeframe timeframe, int candleCount)
    {
        await using var ctx = CreateContext();
        ctx.Set<MLModel>().Add(new MLModel
        {
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = "1.0.0",
            FilePath = $"/tmp/{symbol}.json",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainingSamples = 100,
            TrainedAt = DateTime.UtcNow.AddDays(-10),
            ActivatedAt = DateTime.UtcNow.AddDays(-5),
        });

        var rng = new Random(17);
        decimal price = 1.10m;
        var start = DateTime.UtcNow.AddHours(-candleCount);
        for (int i = 0; i < candleCount; i++)
        {
            decimal delta = (decimal)((rng.NextDouble() - 0.5) * 0.002);
            decimal open = price;
            decimal close = price + delta;
            decimal hi = Math.Max(open, close) + 0.0001m;
            decimal lo = Math.Min(open, close) - 0.0001m;
            ctx.Set<Candle>().Add(new Candle
            {
                Symbol = symbol,
                Timeframe = timeframe,
                Timestamp = start.AddHours(i),
                Open = open, High = hi, Low = lo, Close = close,
                Volume = 1000m + i,
                IsClosed = true,
            });
            price = close;
        }

        await ctx.SaveChangesAsync();
    }

    private sealed class DbContextAccessor(WriteApplicationDbContext context)
        : IReadApplicationDbContext, IWriteApplicationDbContext, IAsyncDisposable
    {
        public DbContext GetDbContext() => context;
        public int SaveChanges() => context.SaveChanges();
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => context.SaveChangesAsync(cancellationToken);
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
