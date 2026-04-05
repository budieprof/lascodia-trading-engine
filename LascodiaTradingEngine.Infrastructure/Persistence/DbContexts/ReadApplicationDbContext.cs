using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedInfrastructure.Persistence;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

/// <summary>
/// Read-only EF Core DbContext used exclusively by CQRS query handlers.
/// Defaults to <see cref="QueryTrackingBehavior.NoTracking"/> for optimal read performance
/// and connects to the read replica connection string (<c>ReadDbConnection</c>).
/// </summary>
public class ReadApplicationDbContext
    : ApplicationDbContext<ReadApplicationDbContext>, IReadApplicationDbContext
{
    public ReadApplicationDbContext(
        DbContextOptions<ReadApplicationDbContext> options,
        IHttpContextAccessor httpContextAccessor)
        : base(options, httpContextAccessor, Assembly.GetExecutingAssembly()) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        // Read context should never track entities — all queries are read-only.
        // Individual handlers can still opt in to tracking with .AsTracking() if needed.
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }
}
