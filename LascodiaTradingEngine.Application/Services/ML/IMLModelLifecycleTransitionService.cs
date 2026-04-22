using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

public interface IMLModelLifecycleTransitionService
{
    Task PromoteChallengerAsync(
        DbContext writeDb,
        long championId,
        long challengerId,
        string symbol,
        Timeframe timeframe,
        CancellationToken cancellationToken);

    Task RejectChallengerAsync(
        DbContext writeDb,
        long challengerId,
        CancellationToken cancellationToken);
}
