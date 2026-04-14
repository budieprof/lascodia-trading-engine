using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;

namespace LascodiaTradingEngine.IntegrationTest.Fixtures;

public sealed class ApiWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public ApiWebApplicationFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public RecordingEventBus EventBus
        => Services.GetRequiredService<IEventBus>() as RecordingEventBus
           ?? throw new InvalidOperationException("RecordingEventBus is not registered.");

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestAuthHandler.AuthorizationHeaderValue);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WriteDbConnection"] = _connectionString,
                ["ConnectionStrings:ReadDbConnection"] = _connectionString,
                ["Encryption:Key"] = "integration-test-encryption-key-1234567890",
                ["JwtSettings:SecretKey"] = "integration-test-secret-key-1234567890",
                ["CorsSettings:AllowedOrigins:0"] = "http://localhost",
                ["WorkerGroups:EnableAll"] = "false",
                ["WorkerGroups:CoreTrading"] = "false",
                ["WorkerGroups:MarketData"] = "false",
                ["WorkerGroups:RiskMonitoring"] = "false",
                ["WorkerGroups:MLTraining"] = "false",
                ["WorkerGroups:MLMonitoring"] = "false",
                ["WorkerGroups:Backtesting"] = "false",
                ["WorkerGroups:Alerts"] = "false",
                ["RabbitMQConfig:Host"] = "localhost",
                ["RabbitMQConfig:Username"] = "guest",
                ["RabbitMQConfig:Password"] = "guest",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IConfigureOptions<CorsOptions>>();
            services.AddCors(options =>
            {
                options.AddPolicy("LascodiaPolicy", policy =>
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader());
            });

            services.RemoveAll<IEventBus>();
            services.AddSingleton<IEventBus, RecordingEventBus>();

            services.RemoveAll<ICurrentUserService>();
            services.AddSingleton<ICurrentUserService, TestCurrentUserService>();

            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    _ => { });
        });
    }
}
