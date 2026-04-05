using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

public interface ISpreadProfileProvider
{
    Func<DateTime, decimal>? BuildSpreadFunction(string symbol, IReadOnlyList<SpreadProfile> profiles);
    Task<IReadOnlyList<SpreadProfile>> GetProfilesAsync(string symbol, CancellationToken ct);
}
