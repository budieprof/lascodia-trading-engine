using Lascodia.Trading.Engine.SharedApplication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

public sealed class EventLogDbContextFactory : IDesignTimeDbContextFactory<EventLogDbContext>
{
    public EventLogDbContext CreateDbContext(string[] args)
    {
        string rootPath = FindRepoRoot();
        string apiPath = Path.Combine(rootPath, "LascodiaTradingEngine.API");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.Exists(apiPath) ? apiPath : rootPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<EventLogDbContext>();
        optionsBuilder.SetPostgresDB<EventLogDbContext>(configuration);

        return new EventLogDbContext(optionsBuilder.Options);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (current.GetFiles("LascodiaTradingEngine.slnx").Any())
                return current.FullName;

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
