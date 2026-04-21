using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

public class ActiveCpcEncoderProviderTest
{
    [Fact]
    public async Task GetAsync_Returns_Active_Encoder_With_NonNull_Weights()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            EmbeddingDim = 16, EncoderBytes = [1, 2, 3], IsActive = true
        });
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 2, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            EmbeddingDim = 16, EncoderBytes = [4, 5, 6], IsActive = false
        });
        await db.SaveChangesAsync();

        var provider = CreateProvider(db, out _);
        var result = await provider.GetAsync("EURUSD", Timeframe.H1, regime: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Id);
    }

    [Fact]
    public async Task GetAsync_Returns_Null_When_No_Active_Row()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            EmbeddingDim = 16, EncoderBytes = [1, 2, 3], IsActive = false
        });
        await db.SaveChangesAsync();

        var provider = CreateProvider(db, out _);
        Assert.Null(await provider.GetAsync("EURUSD", Timeframe.H1, regime: null, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_Skips_Rows_With_Null_EncoderBytes()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            EmbeddingDim = 16, EncoderBytes = null, IsActive = true
        });
        await db.SaveChangesAsync();

        var provider = CreateProvider(db, out _);
        Assert.Null(await provider.GetAsync("EURUSD", Timeframe.H1, regime: null, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_Honours_Symbol_And_Timeframe_Filters()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().AddRange(
            new MLCpcEncoder { Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1, EmbeddingDim = 16, EncoderBytes = [1], IsActive = true },
            new MLCpcEncoder { Id = 2, Symbol = "EURUSD", Timeframe = Timeframe.M15, EmbeddingDim = 16, EncoderBytes = [2], IsActive = true },
            new MLCpcEncoder { Id = 3, Symbol = "GBPUSD", Timeframe = Timeframe.H1, EmbeddingDim = 16, EncoderBytes = [3], IsActive = true }
        );
        await db.SaveChangesAsync();

        var provider = CreateProvider(db, out _);
        var result = await provider.GetAsync("EURUSD", Timeframe.M15, regime: null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Id);
    }

    [Fact]
    public async Task GetAsync_Returns_Regime_Specific_Encoder_When_Available()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().AddRange(
            new MLCpcEncoder { Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1, EmbeddingDim = 16, EncoderBytes = [1], IsActive = true, Regime = null },
            new MLCpcEncoder { Id = 2, Symbol = "EURUSD", Timeframe = Timeframe.H1, EmbeddingDim = 16, EncoderBytes = [2], IsActive = true, Regime = MarketRegime.Trending },
            new MLCpcEncoder { Id = 3, Symbol = "EURUSD", Timeframe = Timeframe.H1, EmbeddingDim = 16, EncoderBytes = [3], IsActive = true, Regime = MarketRegime.Crisis }
        );
        await db.SaveChangesAsync();

        var provider = CreateProvider(db, out _);
        var trending = await provider.GetAsync("EURUSD", Timeframe.H1, MarketRegime.Trending, CancellationToken.None);
        Assert.NotNull(trending);
        Assert.Equal(2, trending!.Id);

        var crisis = await provider.GetAsync("EURUSD", Timeframe.H1, MarketRegime.Crisis, CancellationToken.None);
        Assert.NotNull(crisis);
        Assert.Equal(3, crisis!.Id);

        var global = await provider.GetAsync("EURUSD", Timeframe.H1, regime: null, CancellationToken.None);
        Assert.NotNull(global);
        Assert.Equal(1, global!.Id);
    }

    [Fact]
    public async Task GetAsync_Falls_Back_To_Global_Encoder_When_Regime_Specific_Missing()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 99, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            EmbeddingDim = 16, EncoderBytes = [1], IsActive = true, Regime = null
        });
        await db.SaveChangesAsync();

        var provider = CreateProvider(db, out _);
        var result = await provider.GetAsync("EURUSD", Timeframe.H1, MarketRegime.Ranging, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(99, result!.Id);
        Assert.Null(result.Regime);
    }

    [Fact]
    public async Task GetAsync_Regime_Caches_Are_Keyed_Independently()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().AddRange(
            new MLCpcEncoder { Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1, EmbeddingDim = 16, EncoderBytes = [1], IsActive = true, Regime = MarketRegime.Trending },
            new MLCpcEncoder { Id = 2, Symbol = "EURUSD", Timeframe = Timeframe.H1, EmbeddingDim = 16, EncoderBytes = [2], IsActive = true, Regime = MarketRegime.Ranging }
        );
        await db.SaveChangesAsync();

        var provider = CreateProvider(db, out _);
        var trending = await provider.GetAsync("EURUSD", Timeframe.H1, MarketRegime.Trending, CancellationToken.None);
        var ranging = await provider.GetAsync("EURUSD", Timeframe.H1, MarketRegime.Ranging, CancellationToken.None);

        Assert.Equal(1, trending!.Id);
        Assert.Equal(2, ranging!.Id);
    }

    [Fact]
    public async Task GetAsync_Caches_Result_Across_Subsequent_Calls()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 42, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            EmbeddingDim = 16, EncoderBytes = [1], IsActive = true
        });
        await db.SaveChangesAsync();

        var provider = CreateProvider(db, out var cache);
        var first = await provider.GetAsync("EURUSD", Timeframe.H1, regime: null, CancellationToken.None);
        Assert.NotNull(first);

        // Flip the row to inactive on the tracked context; second call should still return
        // the cached entity because the provider short-circuits before hitting the DB.
        var tracked = await db.Set<MLCpcEncoder>().SingleAsync(e => e.Id == 42);
        tracked.IsActive = false;
        await db.SaveChangesAsync();

        var second = await provider.GetAsync("EURUSD", Timeframe.H1, regime: null, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(42, second!.Id);

        // Evict the cache and prove the provider then sees the flipped state.
        cache.Remove("MLCpcEncoder:EURUSD:H1:global");
        Assert.Null(await provider.GetAsync("EURUSD", Timeframe.H1, regime: null, CancellationToken.None));
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static ActiveCpcEncoderProvider CreateProvider(DbContext db, out IMemoryCache cache)
    {
        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        var provider = services.BuildServiceProvider();

        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 32 });
        return new ActiveCpcEncoderProvider(cache, provider.GetRequiredService<IServiceScopeFactory>());
    }
}
