using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyScreeningEngineFactory))]
internal sealed class StrategyScreeningEngineFactory : IStrategyScreeningEngineFactory
{
    private readonly IBacktestEngine _backtestEngine;
    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IStrategyScreeningArtifactFactory _artifactFactory;
    private readonly TimeProvider _timeProvider;

    public StrategyScreeningEngineFactory(
        IBacktestEngine backtestEngine,
        ILogger<StrategyGenerationWorker> logger,
        IStrategyScreeningArtifactFactory artifactFactory,
        TimeProvider timeProvider)
    {
        _backtestEngine = backtestEngine;
        _logger = logger;
        _artifactFactory = artifactFactory;
        _timeProvider = timeProvider;
    }

    public StrategyScreeningEngine Create(Action<string>? onGateRejection = null)
        => new(_backtestEngine, _logger, onGateRejection, _artifactFactory, _timeProvider);
}
