using System;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Application-level DbContext contract extending the shared <see cref="IDbContext"/>.
/// Serves as the common base for both <see cref="IWriteApplicationDbContext"/> and
/// <see cref="IReadApplicationDbContext"/>.
/// </summary>
public interface IApplicationDbContext : IDbContext
{

}
