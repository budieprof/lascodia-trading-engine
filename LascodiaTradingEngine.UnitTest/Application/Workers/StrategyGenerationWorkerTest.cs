using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class StrategyGenerationWorkerTest
{
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<IServiceScope> _scope = new();
    private readonly Mock<IServiceProvider> _serviceProvider = new();
    private readonly StrategyGenerationWorker _worker;

    public StrategyGenerationWorkerTest()
    {
        _scope.Setup(x => x.ServiceProvider).Returns(_serviceProvider.Object);
        _scopeFactory.Setup(x => x.CreateScope()).Returns(_scope.Object);

        _worker = new StrategyGenerationWorker(
            Mock.Of<ILogger<StrategyGenerationWorker>>(),
            _scopeFactory.Object,
            Mock.Of<IWorkerHealthMonitor>(),
            new StrategyGenerationHealthStore());
    }

    [Fact]
    public async Task ExecutePollAsync_DelegatesToRegisteredScheduler()
    {
        var scheduler = new Mock<IStrategyGenerationScheduler>();
        scheduler
            .Setup(x => x.ExecutePollAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _serviceProvider
            .Setup(x => x.GetService(typeof(IStrategyGenerationScheduler)))
            .Returns(scheduler.Object);

        await _worker.ExecutePollAsync(CancellationToken.None);

        scheduler.Verify(
            x => x.ExecutePollAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecutePollAsync_WhenSchedulerMissing_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _worker.ExecutePollAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunGenerationCycleAsync_DelegatesToRegisteredCycleRunner()
    {
        var scheduler = new Mock<IStrategyGenerationScheduler>();
        var cycleRunner = new Mock<IStrategyGenerationCycleRunner>();
        cycleRunner
            .Setup(x => x.RunAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        scheduler
            .Setup(x => x.ExecuteManualRunAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((callback, token) => callback(token));

        _serviceProvider
            .Setup(x => x.GetService(typeof(IStrategyGenerationScheduler)))
            .Returns(scheduler.Object);
        _serviceProvider
            .Setup(x => x.GetService(typeof(IStrategyGenerationCycleRunner)))
            .Returns(cycleRunner.Object);

        await _worker.RunGenerationCycleAsync(CancellationToken.None);

        scheduler.Verify(
            x => x.ExecuteManualRunAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        cycleRunner.Verify(x => x.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunGenerationCycleAsync_WhenCycleRunnerMissing_Throws()
    {
        var scheduler = new Mock<IStrategyGenerationScheduler>();
        scheduler
            .Setup(x => x.ExecuteManualRunAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((callback, token) => callback(token));
        _serviceProvider
            .Setup(x => x.GetService(typeof(IStrategyGenerationScheduler)))
            .Returns(scheduler.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _worker.RunGenerationCycleAsync(CancellationToken.None));
    }
}
