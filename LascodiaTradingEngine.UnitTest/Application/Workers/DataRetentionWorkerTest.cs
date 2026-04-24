using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using LascodiaTradingEngine.Application;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Workers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class DataRetentionWorkerTest
{
    [Fact]
    public void CalculateDelay_NoFailures_ReturnsBaseInterval()
    {
        var delay = DataRetentionWorker.CalculateDelay(TimeSpan.FromMinutes(5), consecutiveFailures: 0);

        Assert.Equal(TimeSpan.FromMinutes(5), delay);
    }

    [Fact]
    public void CalculateDelay_LongBaseInterval_BacksOff_WithoutShorteningBelowBase()
    {
        var secondFailure = DataRetentionWorker.CalculateDelay(TimeSpan.FromHours(2), consecutiveFailures: 2);
        var fifthFailure = DataRetentionWorker.CalculateDelay(TimeSpan.FromHours(2), consecutiveFailures: 5);

        Assert.Equal(TimeSpan.FromHours(4), secondFailure);
        Assert.Equal(TimeSpan.FromHours(16), fifthFailure);
    }

    [Fact]
    public void CalculateDelay_ShortBaseInterval_UsesMinimumBackoffCeiling()
    {
        var cappedDelay = DataRetentionWorker.CalculateDelay(TimeSpan.FromSeconds(30), consecutiveFailures: 8);

        Assert.Equal(TimeSpan.FromHours(1), cappedDelay);
    }

    [Fact]
    public void GetInitialDelay_ReturnsConfiguredDelay()
    {
        var worker = CreateWorker(new DataRetentionOptions
        {
            InitialDelaySeconds = 42
        });

        Assert.Equal(TimeSpan.FromSeconds(42), worker.GetInitialDelay());
    }

    [Fact]
    public void GetPollIntervalWithJitter_WhenDisabled_ReturnsBaseInterval()
    {
        var worker = CreateWorker(new DataRetentionOptions
        {
            PollIntervalSeconds = 3600,
            PollJitterSeconds = 0
        });

        Assert.Equal(TimeSpan.FromHours(1), worker.GetPollIntervalWithJitter());
    }

    [Fact]
    public async Task RunCycleAsync_WhenLockBusy_ReturnsSkipped_AndDoesNotResolveManager()
    {
        var lockHandle = new Mock<IDistributedLock>(MockBehavior.Strict);
        lockHandle
            .Setup(l => l.TryAcquireAsync(
                "workers:data-retention:cycle",
                TimeSpan.FromSeconds(5),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var worker = CreateWorker(
            new DataRetentionOptions { LockTimeoutSeconds = 5 },
            scopeFactory: scopeFactory.Object,
            distributedLock: lockHandle.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(result.Results);
        scopeFactory.Verify(f => f.CreateScope(), Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_WhenLockAcquired_InvokesManager_AndReturnsResults()
    {
        var retentionManager = new Mock<IDataRetentionManager>(MockBehavior.Strict);
        retentionManager
            .Setup(m => m.EnforceRetentionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RetentionResult("TickRecord", 0, 3, DateTime.UtcNow)
            ]);

        var serviceProvider = new Mock<IServiceProvider>(MockBehavior.Strict);
        serviceProvider
            .Setup(p => p.GetService(typeof(IDataRetentionManager)))
            .Returns(retentionManager.Object);

        var scope = new Mock<IServiceScope>(MockBehavior.Strict);
        scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);
        scope.Setup(s => s.Dispose());

        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var distributedLock = new Mock<IDistributedLock>(MockBehavior.Strict);
        distributedLock
            .Setup(l => l.TryAcquireAsync(
                "workers:data-retention:cycle",
                TimeSpan.FromSeconds(5),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());

        var worker = CreateWorker(
            new DataRetentionOptions { LockTimeoutSeconds = 5 },
            scopeFactory: scopeFactory.Object,
            distributedLock: distributedLock.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Single(result.Results);
        Assert.Equal(3, result.TotalPurged);
        Assert.Equal(1, result.PurgedEntityTypes);
        retentionManager.Verify(m => m.EnforceRetentionAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ConfigureInfrastructureServices_Validates_DataRetentionOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataRetentionOptions:BatchSize"] = "0",
                ["DataRetentionOptions:PollIntervalSeconds"] = "0",
                ["DataRetentionOptions:PollJitterSeconds"] = "-1",
                ["DataRetentionOptions:LockTimeoutSeconds"] = "301"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.ConfigureInfrastructureServices(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<DataRetentionOptions>>();
        var ex = Assert.Throws<OptionsValidationException>(() => _ = options.Value);
        Assert.Contains("BatchSize", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PollIntervalSeconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PollJitterSeconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LockTimeoutSeconds", ex.Message, StringComparison.Ordinal);
    }

    private static DataRetentionWorker CreateWorker(
        DataRetentionOptions options,
        IServiceScopeFactory? scopeFactory = null,
        IDistributedLock? distributedLock = null)
        => new(
            NullLogger<DataRetentionWorker>.Instance,
            scopeFactory ?? Mock.Of<IServiceScopeFactory>(),
            options,
            distributedLock);
}
