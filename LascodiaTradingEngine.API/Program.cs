using LascodiaTradingEngine.Application;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.Cache;
using LascodiaTradingEngine.Infrastructure;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Lascodia.Trading.Engine.SharedApplication;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using Lascodia.Trading.Engine.SharedApplication.Common.Filters;
using System.Reflection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Autofac.Extensions.DependencyInjection;
using Autofac;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(builder => { });

builder.Services.BindConfigurationOptions(builder.Configuration);

builder.Services.ConfigureApplicationServices();
builder.Services.ConfigureDbContexts(builder.Configuration);
builder.Services.AddSharedApplicationDependency(builder.Configuration);

builder.Services.AddAuthentication("Bearer")
.AddJwtBearer("Bearer", options =>
{
    options.RequireHttpsMetadata = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateAudience = false,
        ValidateIssuer = false,
        ValidateIssuerSigningKey = false,
        SignatureValidator = (token, parameters) => new JsonWebToken(token),
    };
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            var failureReason = context.Exception;
            Console.WriteLine($"Authentication failed: {failureReason.Message}");

            context.Response.StatusCode = 401; // Unauthorized
            context.Response.ContentType = "application/json";
            var result = System.Text.Json.JsonSerializer.Serialize(new
            {
                message = "Authentication failed. Reason: " + failureReason.Message
            });
            return context.Response.WriteAsync(result);
        }
    };
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

// Pre-warm the live price cache from the database before workers start.
var priceCache = app.Services.GetRequiredService<ILivePriceCache>();
if (priceCache is InDatabaseLivePriceCache dbCache)
    await dbCache.InitializeAsync();

app.ConfigureEventBus();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.RunAppPipeline<Program, WriteApplicationDbContext, IWriteApplicationDbContext>(services =>
{
    services.DbMigrate<Program, EventLogDbContext, IntegrationEventLogContext<EventLogDbContext>>();
    return true;
});
