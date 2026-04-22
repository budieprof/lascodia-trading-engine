using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Persists terminal signal-level A/B test decisions idempotently.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(ISignalAbTestTerminalResultStore))]
public sealed class SignalAbTestTerminalResultStore : ISignalAbTestTerminalResultStore
{
    public async Task PersistAsync(
        DbContext writeDb,
        AbTestState state,
        AbTestResult result,
        CancellationToken cancellationToken)
    {
        var alreadyPersisted = await TerminalResultExistsAsync(writeDb, state, cancellationToken);

        if (alreadyPersisted)
            return;

        var terminalResult = new MLSignalAbTestResult
        {
            ChampionModelId = state.ChampionModelId,
            ChallengerModelId = state.ChallengerModelId,
            Symbol = state.Symbol,
            Timeframe = state.Timeframe,
            StartedAtUtc = state.StartedAtUtc,
            CompletedAtUtc = DateTime.UtcNow,
            Decision = result.Decision.ToString(),
            Reason = result.Reason,
            ChampionTradeCount = result.ChampionTradeCount,
            ChallengerTradeCount = result.ChallengerTradeCount,
            ChampionAvgPnl = (decimal)result.ChampionAvgPnl,
            ChallengerAvgPnl = (decimal)result.ChallengerAvgPnl,
            ChampionSharpe = (decimal)result.ChampionSharpe,
            ChallengerSharpe = (decimal)result.ChallengerSharpe,
            SprtLogLikelihoodRatio = (decimal)result.SprtLogLikelihoodRatio,
            CovariateImbalanceScore = (decimal)result.CovariateImbalanceScore,
        };

        writeDb.Set<MLSignalAbTestResult>().Add(terminalResult);

        try
        {
            await writeDb.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (!await TerminalResultExistsAfterFailedInsertAsync(writeDb, terminalResult, state, cancellationToken))
                throw;

            // Another worker committed the same terminal result between our existence
            // check and insert. Treat the unique-index conflict as an idempotent win.
        }
    }

    private static async Task<bool> TerminalResultExistsAfterFailedInsertAsync(
        DbContext writeDb,
        MLSignalAbTestResult attemptedResult,
        AbTestState state,
        CancellationToken cancellationToken)
    {
        var entry = writeDb.Entry(attemptedResult);
        if (entry.State != EntityState.Detached)
            entry.State = EntityState.Detached;

        return await TerminalResultExistsAsync(writeDb, state, cancellationToken);
    }

    private static Task<bool> TerminalResultExistsAsync(
        DbContext writeDb,
        AbTestState state,
        CancellationToken cancellationToken)
        => writeDb.Set<MLSignalAbTestResult>()
            .AsNoTracking()
            .AnyAsync(x => x.ChampionModelId == state.ChampionModelId &&
                           x.ChallengerModelId == state.ChallengerModelId &&
                           x.StartedAtUtc == state.StartedAtUtc &&
                           !x.IsDeleted, cancellationToken);
}
