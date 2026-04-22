using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

public interface ISignalAbTestStateBuilder
{
    Task<AbTestState> BuildAsync(
        DbContext readDb,
        long championId,
        long challengerId,
        string symbol,
        Timeframe timeframe,
        DateTime startedAt,
        CancellationToken cancellationToken);
}
