using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationScheduler))]
internal sealed class StrategyGenerationScheduleCoordinator : IStrategyGenerationScheduler
{
    private sealed class SchedulingState
    {
        private const int MaxRetriesPerWindow = 2;

        public DateTime LastRunDateUtc { get; private set; } = DateTime.MinValue;
        public int ConsecutiveFailures { get; private set; }
        public DateTime CircuitBreakerUntilUtc { get; private set; } = DateTime.MinValue;
        public int RetriesThisWindow { get; private set; }
        public DateTime RetryWindowDateUtc { get; private set; } = DateTime.MinValue;
        public bool IsLoaded { get; private set; }
        private bool _wasInWindow;

        public bool IsCircuitBreakerActive(DateTime nowUtc) => nowUtc < CircuitBreakerUntilUtc;
        public bool HasRunToday(DateTime todayUtc) => LastRunDateUtc >= todayUtc;
        public bool RetriesExhausted => RetriesThisWindow >= MaxRetriesPerWindow;

        public void LoadFromPersisted(StrategyGenerationScheduleStateSnapshot snapshot, DateTime todayUtc)
        {
            if (snapshot.LastRunDateUtc != DateTime.MinValue)
                LastRunDateUtc = snapshot.LastRunDateUtc;
            ConsecutiveFailures = snapshot.ConsecutiveFailures;
            CircuitBreakerUntilUtc = snapshot.CircuitBreakerUntilUtc;
            RetryWindowDateUtc = snapshot.RetryWindowDateUtc.Date;
            RetriesThisWindow = RetryWindowDateUtc == todayUtc ? snapshot.RetriesThisWindow : 0;
            IsLoaded = true;
        }

        public void OnSuccess(DateTime todayUtc)
        {
            LastRunDateUtc = todayUtc;
            ConsecutiveFailures = 0;
            RetriesThisWindow = 0;
            RetryWindowDateUtc = DateTime.MinValue;
            _wasInWindow = false;
        }

        public bool OnFailure(DateTime todayUtc, int maxFailures, int backoffDays)
        {
            if (RetryWindowDateUtc != todayUtc)
                RetriesThisWindow = 0;

            ConsecutiveFailures++;
            RetriesThisWindow++;
            RetryWindowDateUtc = todayUtc;

            if (ConsecutiveFailures >= maxFailures)
            {
                CircuitBreakerUntilUtc = todayUtc.AddDays(backoffDays);
                return true;
            }

            return false;
        }

        public void OnRetriesExhausted(DateTime todayUtc)
        {
            LastRunDateUtc = todayUtc;
            RetriesThisWindow = 0;
            RetryWindowDateUtc = DateTime.MinValue;
            _wasInWindow = false;
        }

        public void OnCircuitBreakerSkip(DateTime todayUtc)
        {
            LastRunDateUtc = todayUtc;
            RetriesThisWindow = 0;
            RetryWindowDateUtc = DateTime.MinValue;
            _wasInWindow = false;
        }

        public void MarkInWindow() => _wasInWindow = true;

        public void OnWindowPassed()
        {
            if (!_wasInWindow && RetryWindowDateUtc == DateTime.MinValue)
                return;

            RetriesThisWindow = 0;
            RetryWindowDateUtc = DateTime.MinValue;
            _wasInWindow = false;
        }

        public StrategyGenerationScheduleStateSnapshot ToSnapshot()
            => new(
                LastRunDateUtc,
                ConsecutiveFailures,
                CircuitBreakerUntilUtc,
                RetriesThisWindow,
                RetryWindowDateUtc);
    }

    private const string LockKey = "workers:strategy-generation:cycle";

    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly IStrategyGenerationConfigProvider _configProvider;
    private readonly IStrategyGenerationScheduleStateStore _scheduleStateStore;
    private readonly TimeProvider _timeProvider;
    private readonly SchedulingState _state = new();

    public StrategyGenerationScheduleCoordinator(
        ILogger<StrategyGenerationWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        IStrategyGenerationConfigProvider configProvider,
        IStrategyGenerationScheduleStateStore scheduleStateStore,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _configProvider = configProvider;
        _scheduleStateStore = scheduleStateStore;
        _timeProvider = timeProvider;
    }

    public async Task ExecutePollAsync(Func<CancellationToken, Task> runCycleAsync, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var db = readCtx.GetDbContext();
            var writeDb = writeCtx.GetDbContext();
            var schedule = await _configProvider.LoadScheduleAsync(db, stoppingToken);
            if (!schedule.Enabled)
                return;

            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var todayUtc = nowUtc.Date;

            if (!_state.IsLoaded)
            {
                var persisted = await _scheduleStateStore.LoadAsync(db, stoppingToken);
                _state.LoadFromPersisted(persisted, todayUtc);
            }

            if (nowUtc.Hour != schedule.ScheduleHourUtc || _state.HasRunToday(todayUtc))
            {
                if (nowUtc.Hour != schedule.ScheduleHourUtc)
                    _state.OnWindowPassed();
                return;
            }

            _state.MarkInWindow();

            if (_state.IsCircuitBreakerActive(nowUtc))
            {
                _logger.LogWarning(
                    "StrategyGenerationWorker: circuit breaker active until {Until:u}",
                    _state.CircuitBreakerUntilUtc);
                _metrics.StrategyGenCircuitBreakerTripped.Add(1);
                _state.OnCircuitBreakerSkip(todayUtc);
                await PersistStateAsync(writeDb, writeCtx, stoppingToken);
                return;
            }

            var distributedLock = scope.ServiceProvider.GetService<IDistributedLock>();
            await using var generationLock = distributedLock == null
                ? null
                : await distributedLock.TryAcquireAsync(LockKey, TimeSpan.FromSeconds(2), stoppingToken);
            if (distributedLock != null && generationLock == null)
            {
                _logger.LogInformation(
                    "StrategyGenerationWorker: generation cycle lock is already held elsewhere; skipping this poll");
                return;
            }

            await runCycleAsync(stoppingToken);
            _state.OnSuccess(todayUtc);
            await PersistStateAsync(writeDb, writeCtx, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StrategyGenerationWorker: error during generation cycle");
            _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", "StrategyGenerationWorker"));

            int maxFailures = 3;
            int backoffDays = 2;
            try
            {
                using var cfgScope = _scopeFactory.CreateScope();
                var cfgCtx = cfgScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var generationConfig = await _configProvider.LoadAsync(cfgCtx.GetDbContext(), stoppingToken);
                maxFailures = generationConfig.Config.CircuitBreakerMaxFailures;
                backoffDays = generationConfig.Config.CircuitBreakerBackoffDays;
            }
            catch
            {
                // Use defaults.
            }

            var todayUtc = _timeProvider.GetUtcNow().UtcDateTime.Date;
            bool tripped = _state.OnFailure(todayUtc, maxFailures, backoffDays);
            if (tripped)
            {
                _logger.LogError(
                    "StrategyGenerationWorker: {Failures} failures — circuit breaker until {Until:u}",
                    _state.ConsecutiveFailures,
                    _state.CircuitBreakerUntilUtc);
                _metrics.StrategyGenCircuitBreakerTripped.Add(1);
            }

            if (_state.RetriesExhausted)
            {
                _state.OnRetriesExhausted(todayUtc);
                _logger.LogWarning(
                    "StrategyGenerationWorker: exhausted retries within schedule window — skipping to next day");
            }

            try
            {
                using var persistScope = _scopeFactory.CreateScope();
                var readCtx = persistScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeCtx = persistScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                await PersistStateAsync(writeCtx.GetDbContext(), writeCtx, stoppingToken);
            }
            catch (Exception persistEx)
            {
                _logger.LogWarning(persistEx, "StrategyGenerationWorker: failed to persist scheduling state");
            }
        }
    }

    private async Task PersistStateAsync(
        Microsoft.EntityFrameworkCore.DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct)
    {
        await _scheduleStateStore.SaveAsync(writeDb, _state.ToSnapshot(), ct);
        await writeCtx.SaveChangesAsync(ct);
    }
}
