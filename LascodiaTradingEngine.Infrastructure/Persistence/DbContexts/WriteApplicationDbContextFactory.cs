using System.Reflection;
using Lascodia.Trading.Engine.SharedApplication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

public sealed class WriteApplicationDbContextFactory : IDesignTimeDbContextFactory<WriteApplicationDbContext>
{
    public WriteApplicationDbContext CreateDbContext(string[] args)
    {
        string rootPath = FindRepoRoot();
        string apiPath = Path.Combine(rootPath, "LascodiaTradingEngine.API");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.Exists(apiPath) ? apiPath : rootPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<WriteApplicationDbContext>();
        optionsBuilder.SetPostgresDB<WriteApplicationDbContext>(configuration);

        return new WriteApplicationDbContext(
            optionsBuilder.Options,
            new HttpContextAccessor());
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
