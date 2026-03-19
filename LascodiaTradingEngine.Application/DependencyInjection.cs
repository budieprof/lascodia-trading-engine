using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Lascodia.Trading.Engine.SharedApplication;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Application.Services.Cache;
using LascodiaTradingEngine.Application.Services.BrokerAdapters;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.RiskProfiles.Services;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Application.Services.Alerts.Channels;
using LascodiaTradingEngine.Application.Services.Alerts.Options;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.SignalFilters;
using LascodiaTradingEngine.Application.Services.EconomicCalendar;
using LascodiaTradingEngine.Application.Services.RateLimiting;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Application.Services.Filters;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application;

public static class DependencyInjection
{
    public static IServiceCollection ConfigureApplicationServices(this IServiceCollection services)
    {
        services.ConfigureAppServices(Assembly.GetExecutingAssembly());
        ConfigureInfrastructureServices(services);
        return services;
    }

    public static void ConfigureEventBus(this IApplicationBuilder app)
    {
        var eventBus = app.ApplicationServices.GetRequiredService<IEventBus>();
        eventBus.AutoConfigureEventHandler(Assembly.GetExecutingAssembly());
        var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>(); // Modified line
        lifetime.ApplicationStopping.Register(() =>
        {
            //eventBus.AutoUnconfigureEventHandler(Assembly.GetExecutingAssembly());
        });

        // MEMORY FIX: Explicitly dispose IEventBus on application shutdown
        // This ensures proper cleanup of resources, especially for RabbitMQ/Kafka connections
        lifetime.ApplicationStopped.Register(() =>
        {
            if (eventBus is IDisposable disposable)
            {
                //disposable.Dispose();
            }
        });
    }

    public static void BindConfigurationOptions(this IServiceCollection services, IConfiguration configuration)
    {
        Lascodia.Trading.Engine.SharedApplication.DependencyInjection.AutoRegisterConfigurationOptions(services, configuration, Assembly.GetExecutingAssembly());
    }

    public static IServiceCollection ConfigureInfrastructureServices(this IServiceCollection services)
    {
        // ── Market data ──────────────────────────────────────────────────────────
        services.AddSingleton<ILivePriceCache, InDatabaseLivePriceCache>();
        services.AddSingleton<IBrokerDataFeed, OandaBrokerAdapter>();

        // ── Order execution ──────────────────────────────────────────────────────
        services.AddScoped<IBrokerOrderExecutor, OandaOrderExecutor>();

        // ── Strategy Worker ──────────────────────────────────────────────────────
        // Registered as singleton so the same instance is used both as the hosted
        // service and as the IIntegrationEventHandler resolved by the event bus.
        services.AddSingleton<StrategyWorker>();
        services.AddSingleton<IIntegrationEventHandler<PriceUpdatedIntegrationEvent>>(
            sp => sp.GetRequiredService<StrategyWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<StrategyWorker>());

        // ── Signal → Order Bridge ────────────────────────────────────────────────
        // Consumes TradeSignalCreatedIntegrationEvent, runs risk checks, approves
        // the signal, and creates a Pending order for OrderExecutionWorker to submit.
        services.AddSingleton<SignalOrderBridgeWorker>();
        services.AddSingleton<IIntegrationEventHandler<TradeSignalCreatedIntegrationEvent>>(
            sp => sp.GetRequiredService<SignalOrderBridgeWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<SignalOrderBridgeWorker>());

        // ── Position & Portfolio (Phase 5) ───────────────────────────────────────

        // ── Risk Management (Phase 6) ────────────────────────────────────────────
        services.AddScoped<IRiskChecker, RiskChecker>();

        // ── Backtesting (Phase 7) ────────────────────────────────────────────────
        // Registered as Singleton (not Scoped) so BacktestWorker and OptimizationWorker —
        // both hosted services with Singleton lifetime — can inject it directly.
        services.AddSingleton<IBacktestEngine, BacktestEngine>();

        // ── Alerts (Phase 8) ─────────────────────────────────────────────────────
        services.AddHttpClient();

        // Named HTTP clients with per-channel timeout settings
        services.AddHttpClient("AlertWebhook", (sp, c) =>
        {
            var opts = sp.GetRequiredService<WebhookAlertOptions>();
            c.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 30);
        });
        services.AddHttpClient("AlertTelegram", (sp, c) =>
        {
            var opts = sp.GetRequiredService<TelegramAlertOptions>();
            c.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 10);
        });

        // Channel senders — registered as IAlertChannelSender so dispatcher resolves all via IEnumerable<>
        services.AddScoped<IAlertChannelSender, WebhookAlertSender>();
        services.AddScoped<IAlertChannelSender, EmailAlertSender>();
        services.AddScoped<IAlertChannelSender, TelegramAlertSender>();

        services.AddScoped<IAlertDispatcher, AlertDispatcher>();

        // ── ML Training & Scoring (Phase 9) ─────────────────────────────────────
        services.AddScoped<IMLModelTrainer, BaggedLogisticTrainer>();
        services.AddScoped<IMLSignalScorer, MLSignalScorer>();
        services.AddScoped<ITrainerSelector, TrainerSelector>();

        // ── ML Advanced Services (Phase 12) ──────────────────────────────────
        // Rec #3: Self-supervised pre-training
        services.AddScoped<ISelfSupervisedPretrainer, SelfSupervisedPretrainer>();
        // Rec #8: Counterfactual explanations
        services.AddScoped<ICounterfactualExplainer, CounterfactualExplainer>();
        // Rec #1: TCN architecture trainer (alternative IMLModelTrainer — keyed by LearnerArchitecture)
        services.AddKeyedScoped<IMLModelTrainer, TcnModelTrainer>(LearnerArchitecture.TemporalConvNet);

        // ── Signal Intelligence filters (Phases 12–15) ───────────────────────────
        services.AddScoped<IMultiTimeframeFilter, MultiTimeframeFilter>();
        services.AddScoped<INewsFilter, LascodiaTradingEngine.Application.Services.Filters.NewsFilter>();
        services.AddScoped<IPortfolioCorrelationChecker, PortfolioCorrelationChecker>();
        services.AddSingleton<ISessionFilter, SessionFilter>();

        // ── Market Regime Detection (Phase 17) ───────────────────────────────────
        services.AddScoped<IMarketRegimeDetector, MarketRegimeDetector>();

        // ── Broker Failover (Phase 25) ────────────────────────────────────────────
        services.AddSingleton<IBrokerFailover, BrokerFailoverService>();

        // ── ML Novel Recommendations (Recs #16–35) ────────────────────────────
        // Rec #23: OOD detection — stateless, no DB dependency
        services.AddSingleton<IOodDetector, OodDetector>();
        // Rec #32: Hawkes process signal filter
        services.AddScoped<IHawkesSignalFilter, HawkesSignalFilter>();
        // Rec #35: MinT multi-timeframe reconciler — stateless
        services.AddSingleton<IMinTReconciler, MinTReconciler>();

        // ── ML Novel Recommendations (Recs #36–55) ────────────────────────────
        // Rec #36: VAE latent feature pre-trainer
        services.AddScoped<IVaePretrainer, VaePretrainer>();
        // Rec #49: CPC self-supervised pre-trainer
        services.AddScoped<ICpcPretrainer, CpcPretrainer>();

        // ── ML Novel Recommendations (Recs #56–85) ────────────────────────────
        // Rec #65: Gradient boosting weak learner trainer (keyed alternative to BaggedLogistic)
        services.AddKeyedScoped<IMLModelTrainer, GbmModelTrainer>(LearnerArchitecture.Gbm);

        // ── Production-Grade ML Trainers (A+ tier) ────────────────────────────────
        services.AddKeyedScoped<IMLModelTrainer, ElmModelTrainer>(LearnerArchitecture.Elm);
        services.AddKeyedScoped<IMLModelTrainer, AdaBoostModelTrainer>(LearnerArchitecture.AdaBoost);
        services.AddKeyedScoped<IMLModelTrainer, RocketModelTrainer>(LearnerArchitecture.Rocket);
        services.AddKeyedScoped<IMLModelTrainer, TabNetModelTrainer>(LearnerArchitecture.TabNet);
        services.AddKeyedScoped<IMLModelTrainer, FtTransformerModelTrainer>(LearnerArchitecture.FtTransformer);
        services.AddKeyedScoped<IMLModelTrainer, SmoteModelTrainer>(LearnerArchitecture.Smote);
        services.AddKeyedScoped<IMLModelTrainer, QuantileRfModelTrainer>(LearnerArchitecture.QuantileRf);
        services.AddKeyedScoped<IMLModelTrainer, SvgpModelTrainer>(LearnerArchitecture.Svgp);
        services.AddKeyedScoped<IMLModelTrainer, DannModelTrainer>(LearnerArchitecture.Dann);

        // ── Economic Calendar Worker ──────────────────────────────────────────────
        // Swap StubEconomicCalendarFeed for a real feed implementation before going live.
        services.AddScoped<IEconomicCalendarFeed, StubEconomicCalendarFeed>();

        // ── Rate Limiting (Phase 26) ──────────────────────────────────────────────
        // Singleton so the token-bucket state is shared across all components that
        // call TryAcquireAsync (e.g. OrderExecutionWorker, future broker API helpers).
        services.AddSingleton<IRateLimiter, TokenBucketRateLimiter>();

        // ── Portfolio Optimization ──────────────────────────────────────────────
        services.AddScoped<IPortfolioOptimizer, PortfolioOptimizer>();

        // ── Broker Quality Tracking (latency-aware routing) ─────────────────────
        services.AddSingleton<IBrokerQualityTracker, BrokerQualityTracker>();

        // ── Feature Store (point-in-time guarantees) ────────────────────────────
        services.AddSingleton<IFeatureStore, FeatureStore>();

        // ── A/B Testing Framework ───────────────────────────────────────────────
        services.AddSingleton<IABTestingService, ABTestingService>();

        // ── Engine Monitoring Dashboard ─────────────────────────────────────────
        services.AddScoped<IEngineMonitoringService, EngineMonitoringService>();

        // ── State Reconciliation (disaster recovery) ────────────────────────────
        services.AddScoped<IStateReconciliationService, StateReconciliationService>();

        // ── Multi-Feed Manager (market data redundancy) ─────────────────────────
        services.AddSingleton<IMultiFeedManager, MultiFeedManager>();

        // ── ECN/Prime Brokerage (institutional connectivity) ────────────────────
        services.AddSingleton<IEcnBrokerAdapter, EcnBrokerAdapter>();

        // ── ONNX Inference Engine (GPU-accelerated scoring) ─────────────────────
        services.AddSingleton<IOnnxInferenceEngine, OnnxInferenceEngine>();

        return services;
    }
}
