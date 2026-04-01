using MediatR;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Options;

namespace LascodiaTradingEngine.Application.Common.Behaviors;

/// <summary>
/// MediatR pipeline behavior that randomly injects failures and latency to test resilience.
/// Only active when <see cref="ChaosTestingOptions.Enabled"/> is explicitly true.
/// MUST remain disabled in production environments.
/// </summary>
public class ChaosTestingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ChaosTestingOptions _options;
    private readonly ILogger<ChaosTestingBehavior<TRequest, TResponse>> _logger;

    public ChaosTestingBehavior(
        ChaosTestingOptions options,
        ILogger<ChaosTestingBehavior<TRequest, TResponse>> logger)
    {
        _options = options;
        _logger  = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return await next();

        var requestType = typeof(TRequest).Name;

        // Check if this command type is in scope
        if (_options.AffectedCommands.Count > 0 &&
            !_options.AffectedCommands.Contains(requestType))
            return await next();

        // Random failure injection
        if (_options.FailureRatePct > 0 && Random.Shared.Next(100) < _options.FailureRatePct)
        {
            _logger.LogWarning("ChaosTest: injecting failure into {RequestType}", requestType);
            throw new InvalidOperationException($"[CHAOS TEST] Simulated failure in {requestType}");
        }

        // Random latency injection
        if (_options.MaxLatencyInjectionMs > 0)
        {
            var delayMs = Random.Shared.Next(_options.MaxLatencyInjectionMs);
            if (delayMs > 50) // Only inject noticeable delays
            {
                _logger.LogDebug("ChaosTest: injecting {Delay}ms latency into {RequestType}", delayMs, requestType);
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        return await next();
    }
}
