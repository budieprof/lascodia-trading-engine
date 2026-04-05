using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Read-side EF Core DbContext marker. Injected into query handlers for read-only operations.
/// </summary>
public interface IReadApplicationDbContext : IDbContext { }
