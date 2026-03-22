using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Resolves the active ML model for a given symbol/timeframe, with regime-aware
/// routing and suppression fallback. Extracted from <see cref="MLSignalScorer"/>
/// for testability and single-responsibility.
/// </summary>
internal sealed class MLModelResolver
{
    private static readonly TimeSpan DbQueryTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadApplicationDbContext _context;
    private readonly ILogger _logger;

    internal MLModelResolver(IReadApplicationDbContext context, ILogger logger)
    {
        _context = context;
        _logger  = logger;
    }

    internal async Task<(MLModel? Model, string? CurrentRegime)> ResolveActiveModelAsync(
        TradeSignal signal, Timeframe signalTimeframe, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();
        string? currentRegime = null;
        try
        {
            using var regimeCts = CreateLinkedTimeout(cancellationToken);
            var regimeSnap = await db.Set<MarketRegimeSnapshot>()
                .AsNoTracking()
                .Where(r => r.Symbol == signal.Symbol &&
                            r.Timeframe == signalTimeframe &&
                            !r.IsDeleted)
                .OrderByDescending(r => r.DetectedAt)
                .FirstOrDefaultAsync(regimeCts.Token);

            currentRegime = regimeSnap?.Regime.ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Regime lookup timed out for {Symbol}/{Tf} — using global model",
                signal.Symbol, signalTimeframe);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Regime lookup failed for {Symbol}/{Tf} — using global model",
                signal.Symbol, signalTimeframe);
        }

        MLModel? model = null;
        if (currentRegime is not null)
        {
            model = await db.Set<MLModel>()
                .AsNoTracking()
                .Where(x => x.Symbol      == signal.Symbol &&
                            x.Timeframe   == signalTimeframe &&
                            x.RegimeScope == currentRegime &&
                            x.IsActive    &&
                            !x.IsDeleted)
                .OrderByDescending(x => x.ExpectedValue ?? -1m)
                .ThenByDescending(x => x.ActivatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (model is null)
        {
            model = await db.Set<MLModel>()
                .AsNoTracking()
                .Where(x => x.Symbol      == signal.Symbol &&
                            x.Timeframe   == signalTimeframe &&
                            x.RegimeScope == null &&
                            x.IsActive    &&
                            !x.IsDeleted)
                .OrderByDescending(x => x.ExpectedValue ?? -1m)
                .ThenByDescending(x => x.ActivatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (model?.ModelBytes is not { Length: > 0 })
        {
            _logger.LogDebug(
                "No active ML model for {Symbol}/{Tf} — signal proceeds unscored",
                signal.Symbol, signalTimeframe);
            return (null, currentRegime);
        }

        if (model.IsSuppressed)
        {
            MLModel? fallback = null;
            try
            {
                using var fbCts = CreateLinkedTimeout(cancellationToken);
                fallback = await db.Set<MLModel>()
                    .AsNoTracking()
                    .Where(x => x.Symbol           == signal.Symbol      &&
                                x.Timeframe        == signalTimeframe     &&
                                x.IsFallbackChampion                      &&
                                x.IsActive         && !x.IsDeleted)
                    .OrderByDescending(x => x.ExpectedValue ?? -1m)
                    .ThenByDescending(x => x.ActivatedAt)
                    .FirstOrDefaultAsync(fbCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Fallback champion lookup timed out for {Symbol}/{Tf}",
                    signal.Symbol, signalTimeframe);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Fallback champion lookup failed for {Symbol}/{Tf}",
                    signal.Symbol, signalTimeframe);
            }

            if (fallback?.ModelBytes is not { Length: > 0 })
            {
                _logger.LogDebug(
                    "Scoring suppressed for {Symbol}/{Tf} model {Id} — no fallback champion available.",
                    signal.Symbol, signalTimeframe, model.Id);
                return (null, currentRegime);
            }

            _logger.LogDebug(
                "Scoring suppressed for {Symbol}/{Tf} primary model {Id} — " +
                "routing to fallback champion {FbId}.",
                signal.Symbol, signalTimeframe, model.Id, fallback.Id);
            model = fallback;
        }

        return (model, currentRegime);
    }

    private static CancellationTokenSource CreateLinkedTimeout(CancellationToken parent)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        cts.CancelAfter(DbQueryTimeout);
        return cts;
    }
}
