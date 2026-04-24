using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.COTData;
using LascodiaTradingEngine.Application.Sentiment.Commands.IngestCOTReport;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class COTDataWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_UsesPairMetadata_AndPersistsActualFeedReportDate()
    {
        var mediator = new Mock<IMediator>();
        var feed = new Mock<ICOTDataFeed>(MockBehavior.Strict);
        var commands = new List<IngestCOTReportCommand>();

        mediator
            .Setup(m => m.Send(It.IsAny<IngestCOTReportCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ResponseData<long>>, CancellationToken>((request, _) =>
                commands.Add((IngestCOTReportCommand)request))
            .ReturnsAsync(ResponseData<long>.Init(1, true, "Successful", "00"));

        feed.Setup(f => f.SupportsCurrency(It.IsAny<string>()))
            .Returns<string>(currency => currency is "EUR" or "USD");

        feed.Setup(f => f.GetLatestPublishedReportAsync("EUR", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new COTPositioningData(
                ReportDate: new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc),
                CommercialLong: 10,
                CommercialShort: 11,
                NonCommercialLong: 30,
                NonCommercialShort: 12,
                RetailLong: 6,
                RetailShort: 4,
                TotalOpenInterest: 73));

        feed.Setup(f => f.GetLatestPublishedReportAsync("USD", It.IsAny<CancellationToken>()))
            .ReturnsAsync((COTPositioningData?)null);

        using var provider = BuildProvider(
            mediator.Object,
            feed.Object,
            db =>
            {
                db.Set<CurrencyPair>().AddRange(
                    new CurrencyPair
                    {
                        Id = 1,
                        Symbol = "META_ONLY",
                        BaseCurrency = "EUR",
                        QuoteCurrency = "USD",
                        IsActive = true,
                        IsDeleted = false
                    },
                    new CurrencyPair
                    {
                        Id = 2,
                        Symbol = "XAUUSD",
                        BaseCurrency = "XAU",
                        QuoteCurrency = "USD",
                        IsActive = true,
                        IsDeleted = false
                    });
            });

        var worker = new COTDataWorker(
            NullLogger<COTDataWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeDistributedLock());

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(2, result.ActivePairCount);
        Assert.Equal(3, result.CurrencyCount);
        Assert.Equal(2, result.SupportedCurrencyCount);
        Assert.Equal(1, result.UnsupportedCurrencyCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.RepairedCount);
        Assert.Equal(0, result.UnchangedCount);
        Assert.Equal(1, result.UnavailableCount);
        Assert.Equal(0, result.FetchFailedCount);
        Assert.Equal(0, result.PersistFailedCount);
        Assert.Equal(0, result.FailedCount);

        var command = Assert.Single(commands);
        Assert.Equal("EUR", command.Symbol);
        Assert.Equal(new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc), command.ReportDate);

        feed.Verify(f => f.GetLatestPublishedReportAsync("EUR", It.IsAny<CancellationToken>()), Times.Once);
        feed.Verify(f => f.GetLatestPublishedReportAsync("USD", It.IsAny<CancellationToken>()), Times.Once);
        feed.Verify(f => f.GetLatestPublishedReportAsync("XAU", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_NormalizesFeedReportDateToUtcBeforePersisting()
    {
        var mediator = new Mock<IMediator>();
        var feed = new Mock<ICOTDataFeed>(MockBehavior.Strict);
        IngestCOTReportCommand? command = null;

        mediator
            .Setup(m => m.Send(It.IsAny<IngestCOTReportCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ResponseData<long>>, CancellationToken>((request, _) =>
                command = (IngestCOTReportCommand)request)
            .ReturnsAsync(ResponseData<long>.Init(1, true, "Successful", "00"));

        feed.Setup(f => f.SupportsCurrency(It.IsAny<string>()))
            .Returns<string>(currency => currency == "EUR");

        feed.Setup(f => f.GetLatestPublishedReportAsync("EUR", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new COTPositioningData(
                ReportDate: new DateTime(2026, 5, 25),
                CommercialLong: 10,
                CommercialShort: 11,
                NonCommercialLong: 30,
                NonCommercialShort: 12,
                RetailLong: 6,
                RetailShort: 4,
                TotalOpenInterest: 73));

        using var provider = BuildProvider(
            mediator.Object,
            feed.Object,
            db =>
            {
                db.Set<CurrencyPair>().Add(new CurrencyPair
                {
                    Id = 1,
                    Symbol = "EURUSD",
                    BaseCurrency = "EUR",
                    QuoteCurrency = "ZZZ",
                    IsActive = true,
                    IsDeleted = false
                });
            });

        var worker = new COTDataWorker(
            NullLogger<COTDataWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeDistributedLock());

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.CreatedCount);
        Assert.NotNull(command);
        Assert.Equal(DateTimeKind.Utc, command.ReportDate.Kind);
        Assert.Equal(new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc), command.ReportDate);
    }

    [Fact]
    public async Task RunCycleAsync_RepairsStoredRow_WhenSameDatePayloadDiffers()
    {
        var mediator = new Mock<IMediator>();
        var feed = new Mock<ICOTDataFeed>(MockBehavior.Strict);
        var commands = new List<IngestCOTReportCommand>();

        mediator
            .Setup(m => m.Send(It.IsAny<IngestCOTReportCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ResponseData<long>>, CancellationToken>((request, _) =>
                commands.Add((IngestCOTReportCommand)request))
            .ReturnsAsync(ResponseData<long>.Init(1, true, "Successful", "00"));

        feed.Setup(f => f.SupportsCurrency(It.IsAny<string>()))
            .Returns<string>(currency => currency == "EUR");

        feed.Setup(f => f.GetLatestPublishedReportAsync("EUR", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new COTPositioningData(
                ReportDate: new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc),
                CommercialLong: 150,
                CommercialShort: 120,
                NonCommercialLong: 250,
                NonCommercialShort: 110,
                RetailLong: 35,
                RetailShort: 22,
                TotalOpenInterest: 687));

        using var provider = BuildProvider(
            mediator.Object,
            feed.Object,
            db =>
            {
                db.Set<CurrencyPair>().Add(new CurrencyPair
                {
                    Id = 1,
                    Symbol = "EUR_ONLY",
                    BaseCurrency = "EUR",
                    QuoteCurrency = "ZZZ",
                    IsActive = true,
                    IsDeleted = false
                });

                db.Set<COTReport>().Add(new COTReport
                {
                    Id = 10,
                    Currency = "EUR",
                    ReportDate = new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc),
                    CommercialLong = 100,
                    CommercialShort = 120,
                    NonCommercialLong = 200,
                    NonCommercialShort = 110,
                    RetailLong = 30,
                    RetailShort = 20,
                    TotalOpenInterest = 600,
                    NetNonCommercialPositioning = 90,
                    NetPositioningChangeWeekly = 5,
                    IsDeleted = false
                });
            });

        var worker = new COTDataWorker(
            NullLogger<COTDataWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeDistributedLock());

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.RepairedCount);
        Assert.Equal(0, result.UnchangedCount);
        Assert.Equal(0, result.FetchFailedCount);
        Assert.Equal(0, result.PersistFailedCount);

        var command = Assert.Single(commands);
        Assert.Equal("EUR", command.Symbol);
        Assert.Equal(150m, command.CommercialLong);
        Assert.Equal(687m, command.TotalOpenInterest);
    }

    [Fact]
    public async Task RunCycleAsync_SkipsPublishedRow_WhenStoredPayloadAlreadyMatches()
    {
        var mediator = new Mock<IMediator>();
        var feed = new Mock<ICOTDataFeed>(MockBehavior.Strict);

        mediator
            .Setup(m => m.Send(It.IsAny<IngestCOTReportCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1, true, "Successful", "00"));

        feed.Setup(f => f.SupportsCurrency(It.IsAny<string>()))
            .Returns<string>(currency => currency == "EUR");

        var latest = new COTPositioningData(
            ReportDate: new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc),
            CommercialLong: 150,
            CommercialShort: 120,
            NonCommercialLong: 250,
            NonCommercialShort: 110,
            RetailLong: 35,
            RetailShort: 22,
            TotalOpenInterest: 687);

        feed.Setup(f => f.GetLatestPublishedReportAsync("EUR", It.IsAny<CancellationToken>()))
            .ReturnsAsync(latest);

        using var provider = BuildProvider(
            mediator.Object,
            feed.Object,
            db =>
            {
                db.Set<CurrencyPair>().Add(new CurrencyPair
                {
                    Id = 1,
                    Symbol = "EUR_ONLY",
                    BaseCurrency = "EUR",
                    QuoteCurrency = "ZZZ",
                    IsActive = true,
                    IsDeleted = false
                });

                db.Set<COTReport>().Add(new COTReport
                {
                    Id = 10,
                    Currency = "EUR",
                    ReportDate = latest.ReportDate,
                    CommercialLong = latest.CommercialLong,
                    CommercialShort = latest.CommercialShort,
                    NonCommercialLong = latest.NonCommercialLong,
                    NonCommercialShort = latest.NonCommercialShort,
                    RetailLong = latest.RetailLong,
                    RetailShort = latest.RetailShort,
                    TotalOpenInterest = latest.TotalOpenInterest,
                    NetNonCommercialPositioning = latest.NonCommercialLong - latest.NonCommercialShort,
                    NetPositioningChangeWeekly = 0,
                    IsDeleted = false
                });
            });

        var worker = new COTDataWorker(
            NullLogger<COTDataWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeDistributedLock());

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(0, result.RepairedCount);
        Assert.Equal(1, result.UnchangedCount);

        mediator.Verify(
            m => m.Send(It.IsAny<IngestCOTReportCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_Skips_WhenDistributedLockIsBusy()
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var feed = new Mock<ICOTDataFeed>(MockBehavior.Strict);

        using var provider = BuildProvider(
            mediator.Object,
            feed.Object,
            _ => { });

        var worker = new COTDataWorker(
            NullLogger<COTDataWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeDistributedLock(acquire: false));

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(0, result.ActivePairCount);
        Assert.Equal(0, result.FailedCount);

        mediator.VerifyNoOtherCalls();
        feed.VerifyNoOtherCalls();
    }

    private static ServiceProvider BuildProvider(
        IMediator mediator,
        ICOTDataFeed feed,
        Action<CotWorkerTestDbContext> seed)
    {
        var databaseName = $"cot-worker-{Guid.NewGuid()}";
        var services = new ServiceCollection();

        services.AddDbContext<CotWorkerTestDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddLogging();
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<CotWorkerTestDbContext>());
        services.AddScoped<IMediator>(_ => mediator);
        services.AddScoped<ICOTDataFeed>(_ => feed);
        services.AddScoped<ICOTReportSyncService, COTReportSyncService>();

        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CotWorkerTestDbContext>();
        db.Database.EnsureCreated();
        seed(db);
        db.SaveChanges();

        return provider;
    }

    private sealed class CotWorkerTestDbContext : DbContext, IReadApplicationDbContext
    {
        public CotWorkerTestDbContext(DbContextOptions<CotWorkerTestDbContext> options)
            : base(options)
        {
        }

        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CurrencyPair>();
            modelBuilder.Entity<COTReport>();
        }
    }

    private sealed class FakeDistributedLock(bool acquire = true) : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
        {
            return Task.FromResult<IAsyncDisposable?>(
                acquire ? new Releaser() : null);
        }

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
        {
            return TryAcquireAsync(lockKey, ct);
        }

        private sealed class Releaser : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
