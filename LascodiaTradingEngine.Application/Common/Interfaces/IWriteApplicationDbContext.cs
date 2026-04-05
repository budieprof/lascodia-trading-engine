using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Write-side EF Core DbContext marker. Injected into command handlers for state-modifying operations.
/// </summary>
public interface IWriteApplicationDbContext : IApplicationDbContext { }
