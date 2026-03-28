using LascodiaTradingEngine.Application;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.Cache;
using LascodiaTradingEngine.Infrastructure;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Lascodia.Trading.Engine.SharedApplication;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using Lascodia.Trading.Engine.SharedApplication.Common.Filters;
using System.Reflection;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Autofac.Extensions.DependencyInjection;
using Autofac;
using System.Threading.RateLimiting;
using OpenTelemetry.Metrics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.API.Middleware;
using LascodiaTradingEngine.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Allow background services to fail without stopping the host.
// ShutdownTimeout gives in-flight event handlers (e.g. SignalOrderBridgeWorker,
// OrderFilledEventHandler) up to 30 seconds to complete before force-kill.
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

// ── Thread Pool Tuning ──────────────────────────────────────────────────────
// The engine runs 85+ BackgroundService workers plus Task.Run CPU-bound training.
// The default minimum (Environment.ProcessorCount) causes thread starvation during
// bursts because the pool ramps up slowly (1 thread / 500ms). Pre-warm the pool
// to avoid latency spikes in HTTP requests and time-critical workers.
ThreadPool.SetMinThreads(workerThreads: 100, completionPortThreads: 100);

// ── Structured Logging ──────────────────────────────────────────────────────
// JSON logging in non-Development environments for structured log aggregation.
// Development keeps the default console format for readability.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        options.UseUtcTimestamp = true;
    });
}

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(builder => { });

builder.Services.BindConfigurationOptions(builder.Configuration);

builder.Services.ConfigureApplicationServices();
builder.Services.ConfigureDbContexts(builder.Configuration);
builder.Services.AddSharedApplicationDependency(builder.Configuration);

// Override shared library's size-limited IMemoryCache — ML training and inference
// call IMemoryCache.Set without specifying Size, which throws when SizeLimit is set.
// Remove the shared library's IMemoryCache registration and re-add without SizeLimit.
var cacheDescriptor = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Caching.Memory.IMemoryCache));
if (cacheDescriptor is not null)
    builder.Services.Remove(cacheDescriptor);
builder.Services.AddSingleton<Microsoft.Extensions.Caching.Memory.IMemoryCache>(sp =>
    new Microsoft.Extensions.Caching.Memory.MemoryCache(
        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions
        {
            CompactionPercentage = 0.25,
            ExpirationScanFrequency = TimeSpan.FromMinutes(5)
        }));

// Remove hosted services for disabled worker groups (must run after all registrations)
builder.Services.ApplyWorkerGroupFilter(builder.Configuration);

// ── JWT Authentication ──────────────────────────────────────────────────────
var jwtSection = builder.Configuration.GetSection("JwtSettings");
var secretKey  = jwtSection["SecretKey"]
    ?? throw new InvalidOperationException("JwtSettings:SecretKey is not configured. Set the JWT_SECRET_KEY environment variable.");
var issuer     = jwtSection["Issuer"]   ?? "lascodia-trading-engine";
var audience   = jwtSection["Audience"] ?? "lascodia-trading-engine-api";

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));

builder.Services.AddAuthentication("Bearer")
.AddJwtBearer("Bearer", options =>
{
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer           = true,
        ValidIssuer              = issuer,
        ValidateAudience         = true,
        ValidAudience            = audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey         = signingKey,
        ValidateLifetime         = true,
        ClockSkew                = TimeSpan.FromMinutes(1),
    };
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("JwtAuthentication");

            // Structured audit log for security monitoring
            logger.LogWarning(
                "JWT authentication failed — IP={RemoteIp} Path={Path} Reason={Reason}",
                context.HttpContext.Connection.RemoteIpAddress,
                context.HttpContext.Request.Path,
                context.Exception.GetType().Name);

            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            var result = System.Text.Json.JsonSerializer.Serialize(new
            {
                message = "Authentication failed."
            });
            return context.Response.WriteAsync(result);
        }
    };
});

// ── CORS Policy ─────────────────────────────────────────────────────────────
// Reconfigure the CORS service (already registered by shared library) with a
// named policy. In production, set CorsSettings:AllowedOrigins to your domains.
// In development (empty array), all origins are allowed for convenience.
var allowedOrigins = builder.Configuration.GetSection("CorsSettings:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("LascodiaPolicy", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else if (builder.Environment.IsDevelopment())
        {
            // Development fallback: allow all origins
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        else
        {
            throw new InvalidOperationException(
                "CorsSettings:AllowedOrigins must be configured in production. " +
                "Set at least one allowed origin.");
        }
    });
});

// ── Metrics & Tracing (OpenTelemetry) ────────────────────────────────────────
builder.Services.AddSingleton<TradingMetrics>();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(TradingMetrics.MeterName);
        metrics.AddMeter("Microsoft.AspNetCore.Hosting");
        metrics.AddMeter("Microsoft.AspNetCore.Server.Kestrel");
        metrics.AddMeter("System.Net.Http");
        metrics.AddPrometheusExporter();
    })
    .WithTracing(tracing =>
    {
        tracing.AddSource("LascodiaTradingEngine");
        tracing.AddAspNetCoreInstrumentation(o =>
        {
            o.RecordException = true;
            // Don't trace health check / metrics endpoints
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")
                           && !ctx.Request.Path.StartsWithSegments("/metrics");
        });
        tracing.AddHttpClientInstrumentation();
        // Console exporter in development for local debugging
        if (builder.Environment.IsDevelopment())
            tracing.AddConsoleExporter();
    });

// ── Rate Limiting ────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;

    // Auth endpoints: 10 req/min per IP to resist brute-force
    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    // EA high-frequency endpoints: 10,000 req/min per EA instance.
    // Partition key requires X-EA-Instance-ID header; falls back to IP with a
    // stricter limit (1,000 req/min) to throttle misconfigured EA instances.
    options.AddPolicy("ea", httpContext =>
    {
        var instanceId = httpContext.Request.Headers["X-EA-Instance-ID"].FirstOrDefault();
        if (!string.IsNullOrEmpty(instanceId))
        {
            return RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: instanceId,
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 10_000,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                });
        }

        // Fallback: no instance header — apply a stricter per-IP limit
        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: $"ea-ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 1_000,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
            });
    });

    // Metrics scrape endpoint: 60 req/min per IP (prevents abuse while allowing normal scraping)
    options.AddPolicy("metrics", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

builder.Services.AddSwaggerGen(c =>
{
    c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));
    c.SchemaFilter<SwaggerDefaultValueFilter>();
    c.CustomSchemaIds(type =>
    {
        var fullName = type.FullName;
        if (fullName != null)
        {
            // Use the full namespace to create unique schema IDs
            return fullName.Replace('.', '_').Replace('+', '_');
        }
        return type.Name;
    });
});

var app = builder.Build();

app.ConfigureEventBus();

// Apply the restrictive CORS policy before the shared library's RunAppPipeline
// (which registers a permissive AllowAnyOrigin policy). The first CORS middleware
// in the pipeline handles preflight requests and sets response headers.
// ── Request timing (outermost: captures full pipeline latency) ───────────────
app.UseMiddleware<RequestTimingMiddleware>();

// ── Security middleware (runs before everything else) ────────────────────────
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<InputSanitizationMiddleware>();
app.UseRateLimiter();

app.UseCors("LascodiaPolicy");

// Prometheus scrape endpoint — rate-limited, no auth required
app.MapPrometheusScrapingEndpoint().RequireRateLimiting("metrics");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Detailed health check breakdown endpoint
app.MapHealthChecks("/health/detail", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds,
                exception = e.Value.Exception?.Message,
                tags = e.Value.Tags
            })
        };
        await context.Response.WriteAsJsonAsync(result);
    }
});

app.RunAppPipeline<Program, WriteApplicationDbContext, IWriteApplicationDbContext>(services =>
{
    services.DbMigrate<Program, WriteApplicationDbContext, IWriteApplicationDbContext>();
    services.DbMigrate<Program, EventLogDbContext, IntegrationEventLogContext<EventLogDbContext>>();

    // Seed development data for paper trading when running locally
    if (app.Environment.IsDevelopment())
    {
        var seeder = new DatabaseSeeder(
            services.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>(),
            services.ServiceProvider.GetRequiredService<ILogger<DatabaseSeeder>>());
        seeder.SeedAsync().GetAwaiter().GetResult();
    }

    // Pre-warm the live price cache from the database after migrations have run.
    var priceCache = services.ServiceProvider.GetRequiredService<ILivePriceCache>();
    if (priceCache is InDatabaseLivePriceCache dbCache)
        dbCache.InitializeAsync().GetAwaiter().GetResult();

    return true;
});
