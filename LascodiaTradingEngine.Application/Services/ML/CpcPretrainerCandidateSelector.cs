using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using CandleMarketRegime = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services.ML;

[RegisterService(ServiceLifetime.Scoped, typeof(ICpcPretrainerCandidateSelector))]
public sealed class CpcPretrainerCandidateSelector(TimeProvider? timeProvider = null)
    : ICpcPretrainerCandidateSelector
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<List<CpcPairCandidate>> LoadCandidatePairsAsync(
        DbContext readCtx,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        var pairs = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        if (pairs.Count == 0)
            return [];

        var symbols = pairs.Select(p => p.Symbol).Distinct().ToArray();
        var activeEncoderRows = await readCtx.Set<MLCpcEncoder>()
            .AsNoTracking()
            .Where(e => e.IsActive
                     && !e.IsDeleted
                     && symbols.Contains(e.Symbol))
            .Select(e => new { e.Id, e.Symbol, e.Timeframe, e.Regime, e.TrainedAt, e.InfoNceLoss, e.EncoderType })
            .ToListAsync(ct);

        var configuredEncoderRows = activeEncoderRows
            .Where(e => e.EncoderType == config.EncoderType)
            .ToList();

        var configuredEncoderLookup = configuredEncoderRows
            .GroupBy(e => (e.Symbol, e.Timeframe, e.Regime))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Id).First());

        var currentActiveLookup = activeEncoderRows
            .GroupBy(e => (e.Symbol, e.Timeframe, e.Regime))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.TrainedAt)
                      .ThenByDescending(r => r.Id)
                      .First());

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-config.RetrainIntervalHours);

        var observedRegimesByPair = new Dictionary<(string Symbol, global::LascodiaTradingEngine.Domain.Enums.Timeframe Timeframe), List<CandleMarketRegime>>();
        if (config.TrainPerRegime)
        {
            var observedRegimeRows = await readCtx.Set<MarketRegimeSnapshot>()
                .AsNoTracking()
                .Where(s => symbols.Contains(s.Symbol) && !s.IsDeleted)
                .Select(s => new { s.Symbol, s.Timeframe, s.Regime })
                .Distinct()
                .ToListAsync(ct);

            observedRegimesByPair = observedRegimeRows
                .GroupBy(s => (s.Symbol, s.Timeframe))
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(s => s.Regime).Distinct().OrderBy(r => r).ToList());
        }

        var candidates = new List<CpcPairCandidate>();
        foreach (var pair in pairs)
        {
            var regimes = new List<CandleMarketRegime?> { null };
            if (config.TrainPerRegime &&
                observedRegimesByPair.TryGetValue((pair.Symbol, pair.Timeframe), out var observedRegimes))
            {
                regimes.AddRange(observedRegimes.Cast<CandleMarketRegime?>());
            }

            if (config.TrainPerRegime)
            {
                regimes.AddRange(configuredEncoderRows
                    .Where(e => e.Symbol == pair.Symbol && e.Timeframe == pair.Timeframe && e.Regime is not null)
                    .Select(e => e.Regime)
                    .Distinct());
                regimes = regimes.Distinct().OrderBy(r => r is null ? -1 : (int)r.Value).ToList();
            }

            foreach (var regime in regimes)
            {
                configuredEncoderLookup.TryGetValue((pair.Symbol, pair.Timeframe, regime), out var configuredEncoder);
                currentActiveLookup.TryGetValue((pair.Symbol, pair.Timeframe, regime), out var currentActive);
                if (configuredEncoder is not null && configuredEncoder.TrainedAt > cutoff)
                    continue;

                candidates.Add(new CpcPairCandidate(
                    pair.Symbol,
                    pair.Timeframe,
                    regime,
                    configuredEncoder?.Id,
                    configuredEncoder?.InfoNceLoss,
                    configuredEncoder?.TrainedAt,
                    currentActive?.Id));
            }
        }

        candidates.Sort((a, b) =>
        {
            if (a.PriorTrainedAt is null && b.PriorTrainedAt is null)
                return 0;
            if (a.PriorTrainedAt is null)
                return -1;
            if (b.PriorTrainedAt is null)
                return 1;
            return a.PriorTrainedAt.Value.CompareTo(b.PriorTrainedAt.Value);
        });

        return candidates;
    }
}
