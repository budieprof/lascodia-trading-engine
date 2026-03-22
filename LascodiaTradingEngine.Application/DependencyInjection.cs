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
using LascodiaTradingEngine.Application.Services.EconomicCalendar;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application;

public static class DependencyInjection
{
    public static IServiceCollection ConfigureApplicationServices(this IServiceCollection services)
    {
        services.ConfigureAppServices(Assembly.GetExecutingAssembly());
        services.AutoRegisterAttributedServices(Assembly.GetExecutingAssembly());
        ConfigureInfrastructureServices(services);
        return services;
    }

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

    public static void BindConfigurationOptions(this IServiceCollection services, IConfiguration configuration)
    {
        Lascodia.Trading.Engine.SharedApplication.DependencyInjection.AutoRegisterConfigurationOptions(services, configuration, Assembly.GetExecutingAssembly());
    }

    public static IServiceCollection ConfigureInfrastructureServices(this IServiceCollection services)
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
        services.AddHttpClient("InvestingCalendar", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.Add("User-Agent", "LascodiaTradingEngine/1.0");
        });
        services.AddHttpClient("OandaCalendar", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
        });

        // ── Time abstraction ─────────────────────────────────────────────────────
        services.AddSingleton(TimeProvider.System);

        // ── Economic Calendar Feeds (composite factory) ──────────────────────────
        services.AddScoped<InvestingComCalendarFeed>();
        services.AddScoped<OandaCalendarFeed>();
        services.AddScoped<IEconomicCalendarFeed>(sp =>
            new CompositeCalendarFeed(
                new IEconomicCalendarFeed[]
                {
                    sp.GetRequiredService<InvestingComCalendarFeed>(),
                    sp.GetRequiredService<OandaCalendarFeed>()
                },
                sp.GetRequiredService<ILogger<CompositeCalendarFeed>>()));

        return services;
    }
}
