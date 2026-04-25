using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Lascodia.Trading.Engine.SharedApplication;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Services.Alerts.Options;
using LascodiaTradingEngine.Application.Services.COTData;
using LascodiaTradingEngine.Application.Services.EconomicCalendar;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.Services.MarketData;
using LascodiaTradingEngine.Application.Bridge.Services;
using LascodiaTradingEngine.Application.Common.Behaviors;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.RiskProfiles.Services;
using LascodiaTradingEngine.Application.RiskProfiles.Services.Steps;
using MediatR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

namespace LascodiaTradingEngine.Application;

/// <summary>
/// Registers all Application layer services, workers, HTTP clients, and event bus subscriptions.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers MediatR handlers, FluentValidation validators, AutoMapper profiles, background
    /// workers, HTTP clients, and explicitly configured services for the Application layer.
    /// </summary>
    public static IServiceCollection ConfigureApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureAppServices(Assembly.GetExecutingAssembly());
        services.AutoRegisterAttributedServices(Assembly.GetExecutingAssembly());

        // Remove abstract IHostedService registrations that the shared library's
        // AutoRegisterBackgroundJobs incorrectly picks up (it doesn't filter IsAbstract).
        // Autofac rejects abstract types at container build time.
        var abstractHostedServices = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                     && d.ImplementationType is not null
                     && d.ImplementationType.IsAbstract)
            .ToList();
        foreach (var descriptor in abstractHostedServices)
            services.Remove(descriptor);

        ConfigureInfrastructureServices(services, configuration);
        return services;
    }

    /// <summary>
    /// Subscribes all <c>IIntegrationEventHandler</c> implementations to the event bus
    /// and registers cleanup on application shutdown.
    /// </summary>
    public static void ConfigureEventBus(this IApplicationBuilder app)
    {
        var eventBus = app.ApplicationServices.GetRequiredService<IEventBus>();
        eventBus.AutoConfigureEventHandler(Assembly.GetExecutingAssembly());
        var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            eventBus.AutoUnconfigureEventHandler(Assembly.GetExecutingAssembly());
        });

        lifetime.ApplicationStopped.Register(() =>
        {
            if (eventBus is IDisposable disposable)
            {
                disposable.Dispose();
            }
        });
    }

    /// <summary>
    /// Auto-discovers and binds all <c>ConfigurationOption&lt;T&gt;</c> subclasses to their
    /// matching configuration sections.
    /// </summary>
    public static void BindConfigurationOptions(this IServiceCollection services, IConfiguration configuration)
    {
        Lascodia.Trading.Engine.SharedApplication.DependencyInjection.AutoRegisterConfigurationOptions(services, configuration, Assembly.GetExecutingAssembly());
    }

    /// <summary>
    /// Registers singleton workers, named HTTP clients with retry policies, and infrastructure
    /// services that require explicit wiring (e.g. TCP bridge, candle aggregator, calendar feeds).
    /// </summary>
    public static IServiceCollection ConfigureInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.RemoveAll<MLConformalBreakerOptions>();
        services.AddSingleton<IValidateOptions<MLConformalBreakerOptions>, MLConformalBreakerOptionsValidator>();
        services.AddOptions<MLConformalBreakerOptions>()
            .Bind(configuration.GetSection(nameof(MLConformalBreakerOptions)))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MLConformalBreakerOptions>>().Value);

        services.RemoveAll<MLCorrelatedFailureOptions>();
        services.AddSingleton<IValidateOptions<MLCorrelatedFailureOptions>, MLCorrelatedFailureOptionsValidator>();
        services.AddOptions<MLCorrelatedFailureOptions>()
            .Bind(configuration.GetSection(nameof(MLCorrelatedFailureOptions)))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MLCorrelatedFailureOptions>>().Value);

        services.RemoveAll<MLCorrelatedSignalConflictOptions>();
        services.AddSingleton<IValidateOptions<MLCorrelatedSignalConflictOptions>, MLCorrelatedSignalConflictOptionsValidator>();
        services.AddOptions<MLCorrelatedSignalConflictOptions>()
            .Bind(configuration.GetSection(nameof(MLCorrelatedSignalConflictOptions)))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MLCorrelatedSignalConflictOptions>>().Value);

        services.RemoveAll<MLErgodicityOptions>();
        services.AddSingleton<IValidateOptions<MLErgodicityOptions>, MLErgodicityOptionsValidator>();
        services.AddOptions<MLErgodicityOptions>()
            .Bind(configuration.GetSection(nameof(MLErgodicityOptions)))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MLErgodicityOptions>>().Value);

        services.RemoveAll<CorrelationMatrixOptions>();
        services.AddSingleton<IValidateOptions<CorrelationMatrixOptions>, CorrelationMatrixOptionsValidator>();
        services.AddOptions<CorrelationMatrixOptions>()
            .Bind(configuration.GetSection(nameof(CorrelationMatrixOptions)))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CorrelationMatrixOptions>>().Value);

        services.RemoveAll<TradingDayOptions>();
        services.AddSingleton<IValidateOptions<TradingDayOptions>, TradingDayOptionsValidator>();
        services.AddOptions<TradingDayOptions>()
            .Bind(configuration.GetSection(nameof(TradingDayOptions)))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TradingDayOptions>>().Value);

        services.RemoveAll<DataRetentionOptions>();
        services.AddSingleton<IValidateOptions<DataRetentionOptions>, DataRetentionOptionsValidator>();
        services.AddOptions<DataRetentionOptions>()
            .Bind(configuration.GetSection(nameof(DataRetentionOptions)))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DataRetentionOptions>>().Value);

        // ── Strategy Worker ──────────────────────────────────────────────────────
        // Registered as singleton so the same instance is used both as the hosted
        // service and as the IIntegrationEventHandler resolved by the event bus.
        //
        // The shared library's AutoRegisterEventHandler previously ran (via
        // ConfigureAppServices) and added transient ServiceDescriptors for
        // StrategyWorker/SignalOrderBridgeWorker because they implement
        // IIntegrationEventHandler<T>. Those transient descriptors coexist with
        // the singleton registration below and — depending on the DI container's
        // last-registration semantics — can cause the event bus to resolve a
        // FRESH transient instance for every Handle() call. The transient instance
        // writes the event into ITS OWN private bounded channel, gets disposed
        // at scope close, and the real singleton hosted-service instance waits
        // forever on an empty channel. Result: every tick is silently dropped
        // and zero trade signals are ever generated.
        //
        // Purge those transient descriptors before registering the singletons so
        // the event bus always resolves the hosted-service instance.
        RemoveDescriptors<StrategyWorker>(services);
        RemoveDescriptors<SignalOrderBridgeWorker>(services);
        RemoveHostedServiceDescriptorsByImpl<StrategyWorker>(services);
        RemoveHostedServiceDescriptorsByImpl<SignalOrderBridgeWorker>(services);
        RemoveDescriptorsByImpl<IIntegrationEventHandler<PriceUpdatedIntegrationEvent>, StrategyWorker>(services);
        RemoveDescriptorsByImpl<IIntegrationEventHandler<BacktestCompletedIntegrationEvent>, StrategyWorker>(services);
        RemoveDescriptorsByImpl<IIntegrationEventHandler<StrategyActivatedIntegrationEvent>, StrategyWorker>(services);
        RemoveDescriptorsByImpl<IIntegrationEventHandler<TradeSignalCreatedIntegrationEvent>, SignalOrderBridgeWorker>(services);

        services.AddSingleton<StrategyWorker>();
        services.AddSingleton<IIntegrationEventHandler<PriceUpdatedIntegrationEvent>>(
            sp => sp.GetRequiredService<StrategyWorker>());
        services.AddSingleton<IIntegrationEventHandler<BacktestCompletedIntegrationEvent>>(
            sp => sp.GetRequiredService<StrategyWorker>());
        services.AddSingleton<IIntegrationEventHandler<StrategyActivatedIntegrationEvent>>(
            sp => sp.GetRequiredService<StrategyWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<StrategyWorker>());

        // ── Signal → Order Bridge ────────────────────────────────────────────────
        // Consumes TradeSignalCreatedIntegrationEvent, runs risk checks, approves
        // the signal, and creates a Pending order for OrderExecutionWorker to submit.
        services.AddSingleton<SignalOrderBridgeWorker>();
        services.AddSingleton<IIntegrationEventHandler<TradeSignalCreatedIntegrationEvent>>(
            sp => sp.GetRequiredService<SignalOrderBridgeWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<SignalOrderBridgeWorker>());

        // ── Signal Rejection Auditor (batched) ───────────────────────────────────
        // Single instance forwards to three registrations: the concrete type, the
        // interface binding consumed by callers, and the hosted-service that runs
        // the batched flush loop. A new instance per registration would fragment
        // the bounded channel and drop audit rows on shutdown.
        RemoveDescriptors<Services.SignalRejectionAuditor>(services);
        RemoveDescriptorsByImpl<Common.Interfaces.ISignalRejectionAuditor, Services.SignalRejectionAuditor>(services);
        RemoveHostedServiceDescriptorsByImpl<Services.SignalRejectionAuditor>(services);
        services.AddSingleton<Services.SignalRejectionAuditor>();
        services.AddSingleton<Common.Interfaces.ISignalRejectionAuditor>(
            sp => sp.GetRequiredService<Services.SignalRejectionAuditor>());
        services.AddHostedService(sp => sp.GetRequiredService<Services.SignalRejectionAuditor>());

        // ── Drawdown Monitor ────────────────────────────────────────────────────
        // Hybrid polling + event-driven: regular 60s snapshots + emergency snapshot
        // on large position loss via PositionClosedIntegrationEvent.
        services.AddSingleton<DrawdownMonitorWorker>();
        services.AddSingleton<IIntegrationEventHandler<PositionClosedIntegrationEvent>>(
            sp => sp.GetRequiredService<DrawdownMonitorWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<DrawdownMonitorWorker>());

        // ── Slippage Drift Monitor ─────────────────────────────────────────────
        // Detects strategy crowding by comparing recent-window (7d) vs baseline-window
        // (30d) average slippage per symbol. Rising slippage is a leading indicator
        // of capacity exhaustion, catching edge decay 2-6 weeks before Sharpe does.
        services.AddHostedService<SlippageDriftWorker>();

        // ── Feature-schema-version backfill (one-shot migration) ───────────────
        // Sets ModelSnapshot.FeatureSchemaVersion on legacy JSON blobs that predate
        // the field, removing the runtime inference-by-count dependency. Idempotent —
        // guarded by an EngineConfig flag; subsequent startups are no-ops.
        services.AddHostedService<FeatureSchemaVersionBackfillWorker>();

        // ── Paper-execution monitor (forward-test fill resolution) ─────────────
        // Resolves SL/TP/Timeout on open PaperExecution rows every 5 s. The promotion
        // gate reads closed rows to enforce real-data promotion instead of a
        // backtest-trade proxy. Router is [RegisterService]-auto-wired.
        services.AddHostedService<PaperExecutionMonitorWorker>();

        // ── Revoked-token GC (E10) ─────────────────────────────────────────────
        // Daily sweep that drops RevokedToken rows whose underlying JWT has expired.
        // The blacklist only needs to cover live tokens; without this the table grows
        // unbounded over time.
        services.AddHostedService<RevokedTokenCleanupWorker>();

        // ── ML model auto-rollback ─────────────────────────────────────────────
        // Live-degradation reactor: on detected calibration drift / accuracy floor /
        // retrain-failure breach, swaps the failing active model for its
        // PreviousChampionModelId fallback. Closes the loop drift workers opened.
        services.AddHostedService<MLModelAutoRollbackWorker>();
        services.AddSingleton<IMLConformalCoverageEvaluator, MLConformalCoverageEvaluator>();
        services.AddSingleton<IMLConformalPredictionLogReader, MLConformalPredictionLogReader>();
        services.AddSingleton<IMLConformalCalibrationReader, MLConformalCalibrationReader>();
        services.AddSingleton<IMLConformalBreakerStateStore, MLConformalBreakerStateStore>();

        // ── Portfolio-level optimisation (daily Kelly / HRP) ───────────────────
        // Computes per-strategy allocation weights from realised returns and
        // persists them as PortfolioWeightSnapshot rows. Position sizing reads
        // the latest snapshot per strategy.
        services.AddHostedService<PortfolioOptimizationWorker>();

        // ── Evolutionary strategy generator (daily) ────────────────────────────
        // Mutates the highest-Sharpe Active/Approved strategies and feeds the
        // offspring into the standard screening pipeline. Closes the loop where
        // generation could only ever produce template-driven candidates.
        services.AddHostedService<EvolutionaryGeneratorWorker>();

        // ── Promotion Gate Validator ───────────────────────────────────────────
        // Hard gate between Approved → Active. Enforces DSR, PBO-proxy, TCA-adjusted
        // EV, paper-trade duration, regime-coverage proxy, and max-correlation checks.
        services.AddScoped<LascodiaTradingEngine.Application.Strategies.Services.IPromotionGateValidator,
                           LascodiaTradingEngine.Application.Strategies.Services.PromotionGateValidator>();

        // ── TCA Cost Model Provider ────────────────────────────────────────────
        // Feeds realised per-symbol slippage + spread + commission back into any
        // strategy-evaluation path (backtester, promotion gate, shadow scorer) so
        // the costs used in paper simulation match what live fills actually incur.
        // Closes the #1 "profitable in backtest, bleeds in live" gap.
        services.AddScoped<LascodiaTradingEngine.Application.Services.ITcaCostModelProvider,
                           LascodiaTradingEngine.Application.Services.TcaCostModelProvider>();

        // ── CPCV Validator (minimum-viable: trade-resampling) ──────────────────
        // Produces a Sharpe distribution from C(N, K) chronological-partition
        // resamples of existing trades. Used by PromotionGateValidator to gate on
        // the 25th-percentile Sharpe — a strategy whose fold-Sharpes go deeply
        // negative in some partitions is probably overfit to lucky windows.
        services.AddScoped<LascodiaTradingEngine.Application.Strategies.Services.ICpcvValidator,
                           LascodiaTradingEngine.Application.Strategies.Services.CpcvValidator>();

        // ── Bayesian Edge Posterior ────────────────────────────────────────────
        // Reframes each metric as "posterior P(live Sharpe > 0 | observed)" rather
        // than "observed Sharpe > threshold". Consumed as an additional promotion
        // gate; callers can also query directly for decision-theoretic sizing.
        services.AddScoped<LascodiaTradingEngine.Application.Strategies.Services.IEdgePosterior,
                           LascodiaTradingEngine.Application.Strategies.Services.EdgePosterior>();

        // ── HTTP Clients ─────────────────────────────────────────────────────────
        services.AddHttpClient();

        // Shared retry policy for alert/calendar clients: 3 retries with jittered exponential backoff
        var transientRetryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(3, attempt =>
                TimeSpan.FromSeconds(Math.Pow(2, attempt))
                + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)));

        services.AddHttpClient("AlertWebhook", (sp, c) =>
        {
            var opts = sp.GetRequiredService<WebhookAlertOptions>();
            c.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 30);
        })
        .AddPolicyHandler(transientRetryPolicy);

        services.AddHttpClient("AlertTelegram", (sp, c) =>
        {
            var opts = sp.GetRequiredService<TelegramAlertOptions>();
            c.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 10);
        })
        .AddPolicyHandler(transientRetryPolicy);

        services.AddHttpClient("InvestingCalendar", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.Add("User-Agent", "LascodiaTradingEngine/1.0");
        })
        .AddPolicyHandler(transientRetryPolicy);

        services.AddHttpClient("OandaCalendar", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddPolicyHandler(transientRetryPolicy);

        // DeepSeek V3 API client
        services.AddHttpClient("DeepSeek", (sp, c) =>
        {
            var opts = sp.GetRequiredService<DeepSeekOptions>();
            c.BaseAddress = new Uri(opts.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 30);
            c.DefaultRequestHeaders.Add("Authorization", $"Bearer {opts.ApiKey}");
            c.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddPolicyHandler(transientRetryPolicy);

        // RSS Feed client
        services.AddHttpClient("RssFeed", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.Add("User-Agent", "LascodiaTradingEngine/1.0");
            c.DefaultRequestHeaders.Add("Accept", "application/rss+xml, application/xml, text/xml");
        }).AddPolicyHandler(transientRetryPolicy);
        services.AddHttpClient("ForexFactoryCalendar", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                   | System.Net.DecompressionMethods.Deflate
        })
        .AddPolicyHandler(HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(3, (attempt, response, _) =>
            {
                // Respect Retry-After header from 429 responses when available
                var retryAfter = response?.Result?.Headers?.RetryAfter?.Delta;
                return retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt))
                    + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            }, (_, _, _, _) => Task.CompletedTask));

        // FairEconomy JSON calendar API (mirrors ForexFactory data without Cloudflare)
        services.AddHttpClient("FairEconomyCalendar", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.Add("User-Agent", "LascodiaTradingEngine/1.0");
        })
        .AddPolicyHandler(transientRetryPolicy);

        // ── Correlation Matrix (singleton: both hosted service and provider) ─────
        services.AddSingleton<CorrelationMatrixWorker>();
        services.AddSingleton<ICorrelationMatrixProvider>(sp => sp.GetRequiredService<CorrelationMatrixWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<CorrelationMatrixWorker>());

        // ── EA Ownership Guard ────────────────────────────────────────────────────
        services.AddScoped<IEAOwnershipGuard, EAOwnershipGuard>();

        // ── Candle Aggregator ──────────────────────────────────────────────────────
        // Singleton: must hold state across ticks for the lifetime of the application.
        services.AddSingleton<ICandleAggregator, CandleAggregator>();

        // ── TCP Bridge Worker ─────────────────────────────────────────────────────
        // Singleton so the registry and worker share state. WorkerGroupFilter respects
        // the BridgeOptions.Enabled flag by keeping TcpBridgeWorker in CoreTradingWorkers;
        // the worker itself exits immediately when Enabled=false.
        services.AddSingleton<ITcpBridgeSessionRegistry, TcpBridgeSessionRegistry>();
        services.AddHostedService<TcpBridgeWorker>();

        // ── Time abstraction ─────────────────────────────────────────────────────
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IValidationWorkerIdentity, ValidationWorkerIdentity>();
        services.AddSingleton<IValidationSettingsProvider, ValidationSettingsProvider>();
        services.AddSingleton<IStrategyExecutionSnapshotBuilder, StrategyExecutionSnapshotBuilder>();
        services.AddSingleton<IValidationTradingCalendar, ValidationTradingCalendar>();
        services.AddSingleton<IValidationCandleSeriesGuard, ValidationCandleSeriesGuard>();
        services.AddSingleton<IBacktestAutoScheduler, BacktestAutoScheduler>();
        services.AddSingleton<IBacktestOptionsSnapshotBuilder, BacktestOptionsSnapshotBuilder>();
        services.AddSingleton<IValidationRunFactory, ValidationRunFactory>();
        services.AddSingleton<IAutoWalkForwardWindowPolicy, AutoWalkForwardWindowPolicy>();
        services.AddSingleton<IBacktestRunClaimService, PostgresBacktestRunClaimService>();
        services.AddSingleton<IWalkForwardRunClaimService, PostgresWalkForwardRunClaimService>();

        // ── COT Data Feed (CFTC bulk CSV) ───────────────────────────────────────
        services.AddHttpClient("CftcCOT", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.Add("User-Agent", "LascodiaTradingEngine/1.0");
        })
        .AddPolicyHandler(transientRetryPolicy);
        services.AddScoped<ICOTDataFeed, CftcCOTDataFeed>();

        // ── Economic Calendar Feeds (composite factory) ──────────────────────────
        // FairEconomy JSON API is the primary source (mirrors ForexFactory without Cloudflare).
        // Investing.com is kept as a secondary source for cross-validation.
        // ForexFactory HTML scraper is retained but no longer in the composite — activate
        // it only if needed (ForexFactory's Cloudflare JS challenge currently blocks it).
        services.AddSingleton<ForexFactoryFetchThrottle>();
        services.AddScoped<ForexFactoryCalendarFeed>();
        services.AddScoped<FairEconomyCalendarFeed>();
        services.AddScoped<InvestingComCalendarFeed>();
        services.AddScoped<IEconomicCalendarFeed>(sp =>
            new CompositeCalendarFeed(
                new IEconomicCalendarFeed[]
                {
                    sp.GetRequiredService<FairEconomyCalendarFeed>(),
                    sp.GetRequiredService<InvestingComCalendarFeed>()
                },
                sp.GetRequiredService<ILogger<CompositeCalendarFeed>>()));

        // ── Sentiment Feed (DeepSeek NLP — delegates to IDeepSeekSentimentService) ──────
        services.AddScoped<ISentimentFeed, Services.DeepSeekSentimentFeed>();

        // ── Risk Checker Pipeline ────────────────────────────────────────────────
        // RiskChecker is registered as a concrete type so the pipeline can inject it
        // directly. The pipeline wraps it behind IRiskChecker, running all composable
        // IRiskCheckStep implementations first, then falling back to the monolithic checker.
        services.AddScoped<RiskChecker>();
        services.AddScoped<IRiskCheckStep, MarginRiskCheckStep>();
        services.AddScoped<IRiskCheckStep, ExposureRiskCheckStep>();
        services.AddScoped<IRiskCheckStep, DrawdownRiskCheckStep>();
        // SpreadRiskCheckStep takes a primitive `decimal maxSpreadPips` constructor
        // parameter that Autofac cannot autowire from the container. Resolve the
        // singleton-bound RiskCheckerOptions (auto-registered via
        // AutoRegisterConfigurationOptions) and pass MaxSpreadPips explicitly.
        services.AddScoped<IRiskCheckStep>(sp => new SpreadRiskCheckStep(
            sp.GetRequiredService<RiskCheckerOptions>().MaxSpreadPips));
        services.AddScoped<IRiskChecker, RiskCheckerPipeline>();

        // ── Chaos Testing pipeline behavior (open generic — cannot use [RegisterService]) ──
        // Only register when explicitly enabled to avoid per-request overhead in production.
        // Read directly from IConfiguration to avoid the BuildServiceProvider() anti-pattern
        // (which creates a throwaway DI container and misses later registrations).
        var chaosSection = configuration.GetSection(nameof(ChaosTestingOptions));
        if (chaosSection.GetValue<bool>(nameof(ChaosTestingOptions.Enabled)))
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ChaosTestingBehavior<,>));

        return services;
    }

    /// <summary>
    /// Removes every ServiceDescriptor whose ServiceType matches <typeparamref name="T"/>.
    /// Used to purge transient registrations added by the shared library's
    /// <c>AutoRegisterEventHandler</c> before re-registering the same type as a singleton.
    /// </summary>
    private static void RemoveDescriptors<T>(IServiceCollection services)
    {
        var toRemove = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in toRemove)
            services.Remove(d);
    }

    /// <summary>Removes IHostedService descriptors whose implementation is TImpl.</summary>
    private static void RemoveHostedServiceDescriptorsByImpl<TImpl>(IServiceCollection services)
    {
        var toRemove = services.Where(d =>
            d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && (d.ImplementationType == typeof(TImpl)
                || (d.ImplementationFactory != null && d.ImplementationFactory.Method.ReturnType == typeof(TImpl))
                || d.ImplementationInstance?.GetType() == typeof(TImpl))).ToList();
        foreach (var d in toRemove)
            services.Remove(d);
    }

    /// <summary>Removes descriptors of TService whose implementation is TImpl.</summary>
    private static void RemoveDescriptorsByImpl<TService, TImpl>(IServiceCollection services)
    {
        var toRemove = services.Where(d =>
            d.ServiceType == typeof(TService)
            && (d.ImplementationType == typeof(TImpl)
                || (d.ImplementationFactory != null && d.ImplementationFactory.Method.ReturnType == typeof(TImpl))
                || d.ImplementationInstance?.GetType() == typeof(TImpl))).ToList();
        foreach (var d in toRemove)
            services.Remove(d);
    }
}
