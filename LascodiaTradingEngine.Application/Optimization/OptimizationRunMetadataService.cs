using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationRunMetadataService
{
    private readonly ILogger<OptimizationRunMetadataService> _logger;

    public OptimizationRunMetadataService(ILogger<OptimizationRunMetadataService> logger)
        => _logger = logger;

    internal string SerializeRunMetadata(
        OptimizationRun run,
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        IReadOnlyList<Candle> trainCandles,
        IReadOnlyList<Candle> testCandles,
        int embargoSize,
        string surrogateKind,
        bool resumedFromCheckpoint,
        MarketRegimeEnum? optimizationRegime,
        MarketRegimeEnum? persistenceRegime,
        bool baselineRegimeParamsUsed,
        int warmStarted,
        int totalIters,
        decimal baselineComparisonScore,
        SearchExecutionSummary searchDiagnostics,
        decimal? oosHealthScore,
        bool? autoApproved)
    {
        string? optimizationRegimeText = optimizationRegime?.ToString();
        string dataWindowFingerprint = ComputeDataWindowFingerprint(
            candles,
            trainCandles.Count,
            testCandles.Count,
            embargoSize,
            optimizationRegime);

        var snapshot = new OptimizationRunContracts.RunMetadataSnapshot
        {
            DeterministicSeed = run.DeterministicSeed,
            Surrogate = surrogateKind,
            Symbol = strategy.Symbol,
            Timeframe = strategy.Timeframe,
            CandleFromUtc = candles[0].Timestamp,
            CandleToUtc = candles[^1].Timestamp,
            CandleCount = candles.Count,
            TrainCandles = trainCandles.Count,
            TestCandles = testCandles.Count,
            DataWindowFingerprint = dataWindowFingerprint,
            EmbargoCandles = embargoSize,
            ResumedFromCheckpoint = resumedFromCheckpoint,
            CurrentRegime = optimizationRegimeText,
            OptimizationRegime = optimizationRegimeText,
            PersistenceRegime = persistenceRegime?.ToString(),
            BaselineRegimeParamsUsed = baselineRegimeParamsUsed,
            RecoveryModeUsed = resumedFromCheckpoint ? "checkpoint_resume" : "fresh_start",
            WarmStartedObservations = warmStarted,
            Iterations = totalIters,
            BaselineHealthScore = run.BaselineHealthScore,
            BaselineComparisonScore = baselineComparisonScore,
            OosHealthScore = oosHealthScore,
            AutoApproved = autoApproved,
            SearchSummary = searchDiagnostics,
            SearchAbortReason = searchDiagnostics.AbortReason
        };

        return OptimizationRunContracts.SerializeRunMetadata(snapshot, _logger);
    }

    internal static HashSet<string> ResolveStrategyCurrencies(string symbol, CurrencyPair? pairInfo)
    {
        var currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddCurrency(HashSet<string> target, string? currency)
        {
            if (string.IsNullOrWhiteSpace(currency))
                return;

            var normalized = currency.Trim().ToUpperInvariant();
            if (normalized.Length == 3)
                target.Add(normalized);
        }

        if (pairInfo is not null)
        {
            AddCurrency(currencies, pairInfo.BaseCurrency);
            AddCurrency(currencies, pairInfo.QuoteCurrency);
        }

        if (currencies.Count == 0 && symbol.Length >= 6)
        {
            AddCurrency(currencies, symbol[..3]);
            AddCurrency(currencies, symbol[3..6]);
        }

        return currencies;
    }

    internal static string? ExtractOptimizationRegime(string? runMetadataJson)
        => OptimizationRunContracts.ExtractOptimizationRegime(runMetadataJson);

    internal static string ComputeDataWindowFingerprint(
        IReadOnlyList<Candle> candles,
        int trainCandles,
        int testCandles,
        int embargoCandles,
        MarketRegimeEnum? optimizationRegime)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offsetBasis;

        static ulong Mix(ulong current, byte value)
            => (current ^ value) * prime;

        void MixInt32(int value)
        {
            foreach (var b in BitConverter.GetBytes(value))
                hash = Mix(hash, b);
        }

        void MixInt64(long value)
        {
            foreach (var b in BitConverter.GetBytes(value))
                hash = Mix(hash, b);
        }

        void MixDecimal(decimal value)
        {
            foreach (var part in decimal.GetBits(value))
                MixInt32(part);
        }

        MixInt32(candles.Count);
        MixInt32(trainCandles);
        MixInt32(testCandles);
        MixInt32(embargoCandles);
        MixInt32(optimizationRegime.HasValue ? (int)optimizationRegime.Value : -1);

        foreach (var candle in candles)
        {
            MixInt64(candle.Timestamp.Ticks);
            MixDecimal(candle.Open);
            MixDecimal(candle.High);
            MixDecimal(candle.Low);
            MixDecimal(candle.Close);
        }

        return hash.ToString("X16");
    }
}
