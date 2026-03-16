using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Lascodia.Trading.Engine.SharedApplication;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

namespace LascodiaTradingEngine.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection ConfigureDbContexts(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<WriteApplicationDbContext>(options =>
            options.SetDB<WriteApplicationDbContext>(configuration, "WriteDbConnection"));
        services.AddScoped<IWriteApplicationDbContext>(p => p.GetRequiredService<WriteApplicationDbContext>());

        services.AddDbContext<ReadApplicationDbContext>(options =>
            options.SetDB<ReadApplicationDbContext>(configuration, "ReadDbConnection"));
        services.AddScoped<IReadApplicationDbContext>(p => p.GetRequiredService<ReadApplicationDbContext>());

        return services;
    }
}
