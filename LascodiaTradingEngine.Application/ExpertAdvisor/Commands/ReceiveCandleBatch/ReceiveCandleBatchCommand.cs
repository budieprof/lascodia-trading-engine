using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveCandleBackfill;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveCandleBatch;

// ── DTO ──────────────────────────────────────────────────────────────────────

/// <summary>
/// A single OHLCV candle within a batch submission. May represent a forming or closed bar.
/// </summary>
public class CandleBatchItem
{
    /// <summary>Instrument symbol (e.g. "EURUSD"). Normalised to upper-case during processing.</summary>
    public required string Symbol    { get; set; }

    /// <summary>Bar timeframe (e.g. "M1", "H1", "D1"). Must parse to the Timeframe enum.</summary>
    public required string Timeframe { get; set; }

    /// <summary>Opening price of the candle.</summary>
    public decimal  Open      { get; set; }

    /// <summary>Highest price during the candle period.</summary>
    public decimal  High      { get; set; }

    /// <summary>Lowest price during the candle period.</summary>
    public decimal  Low       { get; set; }

    /// <summary>Closing (or current) price of the candle.</summary>
    public decimal  Close     { get; set; }

    /// <summary>Tick volume for the candle period.</summary>
    public decimal  Volume    { get; set; }

    /// <summary>Candle open time as reported by the EA.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>True if the bar is complete; false if still forming.</summary>
    public bool     IsClosed  { get; set; }
}

// ── Command ──────────────────────────────────────────────────────────────────

/// <summary>
/// Receives a batch of OHLCV candles (potentially across multiple symbols and timeframes) from an EA instance.
/// Each candle is upserted by its natural key (Symbol + Timeframe + Timestamp). Returns the count of processed candles.
/// </summary>
public class ReceiveCandleBatchCommand : IRequest<ResponseData<int>>
{
    /// <summary>Unique identifier of the EA instance sending the batch.</summary>
    public required string InstanceId { get; set; }

    /// <summary>List of candles to upsert. Capped at 10,000 items per request.</summary>
    public List<CandleBatchItem> Candles { get; set; } = new();
}

// ── Validator ────────────────────────────────────────────────────────────────

/// <summary>
/// Validates InstanceId is non-empty, Candles is not null, and batch size does not exceed 10,000 items.
/// </summary>
public class ReceiveCandleBatchCommandValidator : AbstractValidator<ReceiveCandleBatchCommand>
{
    public ReceiveCandleBatchCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Candles)
            .NotNull().WithMessage("Candles list cannot be null")
            .Must(c => c.Count <= 10000).WithMessage("Candle batch cannot exceed 10000 items");
    }
}

// ── Handler ──────────────────────────────────────────────────────────────────

/// <summary>
/// Handles candle batch ingestion. Iterates each candle, parses the timeframe, and upserts by
/// (Symbol, Timeframe, Timestamp). Invalid timeframes are silently skipped. All changes are
/// flushed in a single SaveChangesAsync call for efficiency.
/// </summary>
public class ReceiveCandleBatchCommandHandler : IRequestHandler<ReceiveCandleBatchCommand, ResponseData<int>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IMarketDataAnomalyDetector _anomalyDetector;
    private readonly ILogger<ReceiveCandleBatchCommandHandler> _logger;

    public ReceiveCandleBatchCommandHandler(
        IWriteApplicationDbContext context,
        IMarketDataAnomalyDetector anomalyDetector,
        ILogger<ReceiveCandleBatchCommandHandler> logger)
    {
        _context         = context;
        _anomalyDetector = anomalyDetector;
        _logger          = logger;
    }

    public async Task<ResponseData<int>> Handle(ReceiveCandleBatchCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();
        var candleSet = dbContext.Set<Domain.Entities.Candle>();
        int processed = 0;
        int skippedTimeframes = 0;
        int skippedAnomalies = 0;

        // Parse, normalize, and validate all items up-front
        var parsedItems = new List<(string Symbol, Timeframe Timeframe, DateTime NormalizedTs, CandleBatchItem Item)>();

        foreach (var item in request.Candles)
        {
            if (!Enum.TryParse<Timeframe>(item.Timeframe, ignoreCase: true, out var tf))
            {
                skippedTimeframes++;
                continue;
            }

            // OHLC anomaly detection — same check as ReceiveCandleCommand
            var qualityResult = _anomalyDetector.ValidateCandle(
                item.Open, item.High, item.Low, item.Close,
                (long)item.Volume, item.Timestamp, null);

            if (!qualityResult.IsValid)
            {
                skippedAnomalies++;
                _logger.LogWarning(
                    "ReceiveCandleBatch: candle rejected for {Symbol} {Timeframe} at {Timestamp}: {Reason}",
                    item.Symbol, item.Timeframe, item.Timestamp, qualityResult.Description);
                continue;
            }

            // Normalize timestamp to timeframe boundary (prevents near-duplicate rows)
            var normalizedTs = ReceiveCandleBackfillCommandHandler.NormalizeTimestamp(item.Timestamp, tf);

            parsedItems.Add((Symbol: item.Symbol.ToUpperInvariant(), Timeframe: tf, NormalizedTs: normalizedTs, Item: item));
        }

        if (skippedTimeframes > 0)
            _logger.LogWarning("ReceiveCandleBatch: skipped {Count} candle(s) with unrecognised timeframe", skippedTimeframes);

        // Batch-load all existing candles matching the incoming natural keys to avoid N+1 queries.
        var groups = parsedItems.GroupBy(x => (x.Symbol, x.Timeframe));
        var existingByKey = new Dictionary<(string Symbol, Timeframe Tf, DateTime Ts), Domain.Entities.Candle>();

        foreach (var group in groups)
        {
            var timestamps = group.Select(g => g.NormalizedTs).Distinct().ToList();
            var existing = await candleSet
                .Where(x => x.Symbol == group.Key.Symbol
                          && x.Timeframe == group.Key.Timeframe
                          && timestamps.Contains(x.Timestamp)
                          && !x.IsDeleted)
                .ToListAsync(cancellationToken);

            foreach (var candle in existing)
                existingByKey[(candle.Symbol, candle.Timeframe, candle.Timestamp)] = candle;
        }

        // Upsert using the pre-loaded lookup
        foreach (var (symbol, timeframe, normalizedTs, item) in parsedItems)
        {
            if (existingByKey.TryGetValue((symbol, timeframe, normalizedTs), out var existingCandle))
            {
                existingCandle.Open     = item.Open;
                existingCandle.High     = item.High;
                existingCandle.Low      = item.Low;
                existingCandle.Close    = item.Close;
                existingCandle.Volume   = item.Volume;
                existingCandle.IsClosed = item.IsClosed;
            }
            else
            {
                var newCandle = new Domain.Entities.Candle
                {
                    Symbol    = symbol,
                    Timeframe = timeframe,
                    Open      = item.Open,
                    High      = item.High,
                    Low       = item.Low,
                    Close     = item.Close,
                    Volume    = item.Volume,
                    Timestamp = normalizedTs,
                    IsClosed  = item.IsClosed,
                };
                await candleSet.AddAsync(newCandle, cancellationToken);
                existingByKey[(symbol, timeframe, normalizedTs)] = newCandle;
            }
            processed++;
        }

        await _context.SaveChangesAsync(cancellationToken);

        string message = skippedAnomalies > 0 || skippedTimeframes > 0
            ? $"Processed {processed} candles ({skippedTimeframes} bad timeframe(s), {skippedAnomalies} anomaly/anomalies skipped)"
            : "Successful";
        return ResponseData<int>.Init(processed, true, message, "00");
    }
}
