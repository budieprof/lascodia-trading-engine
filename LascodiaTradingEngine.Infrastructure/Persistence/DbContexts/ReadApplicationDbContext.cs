using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedInfrastructure.Persistence;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

public class ReadApplicationDbContext
    : ApplicationDbContext<ReadApplicationDbContext>, IReadApplicationDbContext
{
    public ReadApplicationDbContext(
        DbContextOptions<ReadApplicationDbContext> options,
        IHttpContextAccessor httpContextAccessor)
        : base(options, httpContextAccessor, Assembly.GetExecutingAssembly()) { }

}
