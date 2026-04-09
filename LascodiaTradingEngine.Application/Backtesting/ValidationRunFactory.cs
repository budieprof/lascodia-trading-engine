using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting;

public readonly record struct BacktestQueueRequest(
    long StrategyId,
    string Symbol,
    Timeframe Timeframe,
    DateTime FromDate,
    DateTime ToDate,
    decimal InitialBalance,
    ValidationQueueSource QueueSource,
    int Priority = 0,
    long? SourceOptimizationRunId = null,
    string? ParametersSnapshotJson = null,
    string? ValidationQueueKey = null,
    string? BacktestOptionsSnapshotJson = null);

public readonly record struct WalkForwardQueueRequest(
    long StrategyId,
    string Symbol,
    Timeframe Timeframe,
    DateTime FromDate,
    DateTime ToDate,
    int InSampleDays,
    int OutOfSampleDays,
    decimal InitialBalance,
    ValidationQueueSource QueueSource,
    int Priority = 0,
    bool ReOptimizePerFold = false,
    long? SourceOptimizationRunId = null,
    string? ParametersSnapshotJson = null,
    string? ValidationQueueKey = null,
    string? BacktestOptionsSnapshotJson = null);

public interface IBacktestOptionsSnapshotBuilder
{
    Task<BacktestOptionsSnapshot> BuildAsync(
        DbContext writeDb,
        string symbol,
        CancellationToken ct);
}

public interface IValidationRunFactory
{
    Task<BacktestRun> BuildBacktestRunAsync(
        DbContext writeDb,
        BacktestQueueRequest request,
        CancellationToken ct);

    Task<WalkForwardRun> BuildWalkForwardRunAsync(
        DbContext writeDb,
        WalkForwardQueueRequest request,
        CancellationToken ct);
}

internal sealed class BacktestOptionsSnapshotBuilder : IBacktestOptionsSnapshotBuilder
{
    private readonly IValidationSettingsProvider _settingsProvider;
    private readonly ILogger<BacktestOptionsSnapshotBuilder> _logger;

    public BacktestOptionsSnapshotBuilder(
        IValidationSettingsProvider settingsProvider,
        ILogger<BacktestOptionsSnapshotBuilder> logger)
    {
        _settingsProvider = settingsProvider;
        _logger = logger;
    }

    public async Task<BacktestOptionsSnapshot> BuildAsync(
        DbContext writeDb,
        string symbol,
        CancellationToken ct)
    {
        CurrencyPair? pairInfo = null;
        var currencyPairSet = TryGetSet<CurrencyPair>(writeDb);
        if (currencyPairSet != null)
        {
            pairInfo = await currencyPairSet
                .FirstOrDefaultAsync(pair => pair.Symbol == symbol && !pair.IsDeleted, ct);
        }

        var assetClass = StrategyGenerationHelpers.ClassifyAsset(symbol, pairInfo);
        decimal pointSize = pairInfo != null && pairInfo.DecimalPlaces > 0
            ? 1.0m / (decimal)Math.Pow(10, pairInfo.DecimalPlaces)
            : StrategyGenerationHelpers.GetDefaultPointSize(assetClass);

        decimal configuredSpreadPoints = await _settingsProvider.GetDecimalAsync(
            writeDb,
            _logger,
            "Backtest:SpreadPoints",
            20.0m,
            ct,
            minInclusive: 0m);
        decimal commissionPerLot = await _settingsProvider.GetDecimalAsync(
            writeDb,
            _logger,
            "Backtest:CommissionPerLot",
            7.0m,
            ct,
            minInclusive: 0m);
        decimal slippagePips = await _settingsProvider.GetDecimalAsync(
            writeDb,
            _logger,
            "Backtest:SlippagePips",
            1.0m,
            ct,
            minInclusive: 0m);
        decimal effectiveSpreadPoints = pairInfo?.SpreadPoints > 0
            ? Math.Max(configuredSpreadPoints, (decimal)pairInfo.SpreadPoints * 1.5m)
            : configuredSpreadPoints;

        List<SpreadBucketSnapshot> spreadBuckets = [];
        var spreadProfileSet = TryGetSet<SpreadProfile>(writeDb);
        if (spreadProfileSet != null)
        {
            spreadBuckets = await spreadProfileSet
                .Where(profile => profile.Symbol == symbol && !profile.IsDeleted)
                .OrderBy(profile => profile.HourUtc)
                .ThenBy(profile => profile.DayOfWeek)
                .Select(profile => new SpreadBucketSnapshot
                {
                    HourUtc = profile.HourUtc,
                    DayOfWeek = profile.DayOfWeek,
                    SpreadPriceUnits = profile.SpreadP50,
                })
                .ToListAsync(ct);
        }

        return new BacktestOptionsSnapshot
        {
            SpreadPriceUnits = pointSize * effectiveSpreadPoints,
            CommissionPerLot = StrategyGenerationHelpers.ScaleCommissionForAssetClass(commissionPerLot, assetClass),
            SlippagePriceUnits = pointSize * slippagePips * 10m,
            ContractSize = pairInfo?.ContractSize ?? StrategyGenerationHelpers.GetDefaultContractSize(assetClass),
            SpreadBuckets = spreadBuckets,
        };
    }

    private static DbSet<TEntity>? TryGetSet<TEntity>(DbContext dbContext)
        where TEntity : class
    {
        try
        {
            return dbContext.Set<TEntity>();
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class ValidationRunFactory : IValidationRunFactory
{
    private readonly IBacktestOptionsSnapshotBuilder _optionsSnapshotBuilder;
    private readonly TimeProvider _timeProvider;

    public ValidationRunFactory(
        IBacktestOptionsSnapshotBuilder optionsSnapshotBuilder,
        TimeProvider timeProvider)
    {
        _optionsSnapshotBuilder = optionsSnapshotBuilder;
        _timeProvider = timeProvider;
    }

    public async Task<BacktestRun> BuildBacktestRunAsync(
        DbContext writeDb,
        BacktestQueueRequest request,
        CancellationToken ct)
    {
        string normalizedSymbol = request.Symbol.ToUpperInvariant();
        string? parameterSnapshot = NormalizeParameters(request.ParametersSnapshotJson);
        string optionsSnapshotJson = request.BacktestOptionsSnapshotJson
            ?? JsonSerializer.Serialize(await _optionsSnapshotBuilder.BuildAsync(writeDb, normalizedSymbol, ct));
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        return new BacktestRun
        {
            StrategyId = request.StrategyId,
            Symbol = normalizedSymbol,
            Timeframe = request.Timeframe,
            FromDate = request.FromDate,
            ToDate = request.ToDate,
            InitialBalance = request.InitialBalance,
            Status = RunStatus.Queued,
            Priority = request.Priority,
            SourceOptimizationRunId = request.SourceOptimizationRunId,
            ParametersSnapshotJson = parameterSnapshot,
            BacktestOptionsSnapshotJson = optionsSnapshotJson,
            ValidationQueueKey = request.ValidationQueueKey,
            QueueSource = request.QueueSource,
            StartedAt = nowUtc,
            QueuedAt = nowUtc,
            AvailableAt = nowUtc,
            CompletedAt = null,
            ErrorMessage = null,
            FailureCode = null,
            FailureDetailsJson = null,
            ClaimedAt = null,
            ClaimedByWorkerId = null,
            ExecutionStartedAt = null,
            LastAttemptAt = null,
            LastHeartbeatAt = null,
            ExecutionLeaseExpiresAt = null,
            ExecutionLeaseToken = null,
            RetryCount = 0,
            ResultJson = null,
            TotalTrades = null,
            WinRate = null,
            ProfitFactor = null,
            MaxDrawdownPct = null,
            SharpeRatio = null,
            FinalBalance = null,
            TotalReturn = null,
        };
    }

    public async Task<WalkForwardRun> BuildWalkForwardRunAsync(
        DbContext writeDb,
        WalkForwardQueueRequest request,
        CancellationToken ct)
    {
        string normalizedSymbol = request.Symbol.ToUpperInvariant();
        string? parameterSnapshot = NormalizeParameters(request.ParametersSnapshotJson);
        string optionsSnapshotJson = request.BacktestOptionsSnapshotJson
            ?? JsonSerializer.Serialize(await _optionsSnapshotBuilder.BuildAsync(writeDb, normalizedSymbol, ct));
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        return new WalkForwardRun
        {
            StrategyId = request.StrategyId,
            Symbol = normalizedSymbol,
            Timeframe = request.Timeframe,
            FromDate = request.FromDate,
            ToDate = request.ToDate,
            InSampleDays = request.InSampleDays,
            OutOfSampleDays = request.OutOfSampleDays,
            ReOptimizePerFold = request.ReOptimizePerFold,
            InitialBalance = request.InitialBalance,
            Status = RunStatus.Queued,
            Priority = request.Priority,
            SourceOptimizationRunId = request.SourceOptimizationRunId,
            ParametersSnapshotJson = parameterSnapshot,
            BacktestOptionsSnapshotJson = optionsSnapshotJson,
            ValidationQueueKey = request.ValidationQueueKey,
            QueueSource = request.QueueSource,
            StartedAt = nowUtc,
            QueuedAt = nowUtc,
            AvailableAt = nowUtc,
            ClaimedAt = null,
            ClaimedByWorkerId = null,
            ExecutionStartedAt = null,
            LastAttemptAt = null,
            LastHeartbeatAt = null,
            ExecutionLeaseExpiresAt = null,
            ExecutionLeaseToken = null,
            CompletedAt = null,
            ErrorMessage = null,
            FailureCode = null,
            FailureDetailsJson = null,
            RetryCount = 0,
            AverageOutOfSampleScore = null,
            ScoreConsistency = null,
            WindowResultsJson = null,
        };
    }

    private static string? NormalizeParameters(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
            return null;

        return CanonicalParameterJson.Normalize(parametersJson);
    }
}
