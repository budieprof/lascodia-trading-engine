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
using MediatR;
using Microsoft.Extensions.Logging;
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

        // ── COT Data Feed (CFTC bulk CSV) ───────────────────────────────────────
        services.AddHttpClient("CftcCOT", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.Add("User-Agent", "LascodiaTradingEngine/1.0");
        })
        .AddPolicyHandler(transientRetryPolicy);
        services.AddScoped<ICOTDataFeed, CftcCOTDataFeed>();

        // ── Economic Calendar Feeds (composite factory) ──────────────────────────
        services.AddSingleton<ForexFactoryFetchThrottle>();
        services.AddScoped<ForexFactoryCalendarFeed>();
        services.AddScoped<InvestingComCalendarFeed>();
        services.AddScoped<IEconomicCalendarFeed>(sp =>
            new CompositeCalendarFeed(
                new IEconomicCalendarFeed[]
                {
                    sp.GetRequiredService<ForexFactoryCalendarFeed>(),
                    sp.GetRequiredService<InvestingComCalendarFeed>()
                },
                sp.GetRequiredService<ILogger<CompositeCalendarFeed>>()));

        // ── Sentiment Feed (DeepSeek NLP — delegates to IDeepSeekSentimentService) ──────
        services.AddScoped<ISentimentFeed, Services.DeepSeekSentimentFeed>();

        // ── Chaos Testing pipeline behavior (open generic — cannot use [RegisterService]) ──
        // Only register when explicitly enabled to avoid per-request overhead in production.
        // Read directly from IConfiguration to avoid the BuildServiceProvider() anti-pattern
        // (which creates a throwaway DI container and misses later registrations).
        var chaosSection = configuration.GetSection(nameof(ChaosTestingOptions));
        if (chaosSection.GetValue<bool>(nameof(ChaosTestingOptions.Enabled)))
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ChaosTestingBehavior<,>));

        return services;
    }
}
