using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedInfrastructure.Persistence;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

/// <summary>
/// Read-write EF Core DbContext used exclusively by CQRS command handlers.
/// Connects to the primary database connection string (<c>WriteDbConnection</c>) and
/// enables change tracking for transactional writes.
/// </summary>
public class WriteApplicationDbContext
    : ApplicationDbContext<WriteApplicationDbContext>, IWriteApplicationDbContext
{
    public WriteApplicationDbContext(
        DbContextOptions<WriteApplicationDbContext> options,
        IHttpContextAccessor httpContextAccessor)
        : base(options, httpContextAccessor, Assembly.GetExecutingAssembly()) { }
}
