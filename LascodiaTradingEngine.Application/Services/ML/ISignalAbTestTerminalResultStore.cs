using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public interface ISignalAbTestTerminalResultStore
{
    Task PersistAsync(
        DbContext writeDb,
        AbTestState state,
        AbTestResult result,
        CancellationToken cancellationToken);
}
