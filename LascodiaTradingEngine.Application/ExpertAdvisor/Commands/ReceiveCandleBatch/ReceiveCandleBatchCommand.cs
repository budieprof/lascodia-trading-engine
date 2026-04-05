using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
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

    public ReceiveCandleBatchCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<int>> Handle(ReceiveCandleBatchCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();
        var candleSet = dbContext.Set<Domain.Entities.Candle>();
        int processed = 0;

        foreach (var item in request.Candles)
        {
            var symbol    = item.Symbol.ToUpperInvariant();
            if (!Enum.TryParse<Timeframe>(item.Timeframe, ignoreCase: true, out var timeframe))
                continue;

            var existing = await candleSet
                .FirstOrDefaultAsync(
                    x => x.Symbol == symbol
                      && x.Timeframe == timeframe
                      && x.Timestamp == item.Timestamp
                      && !x.IsDeleted,
                    cancellationToken);

            if (existing is not null)
            {
                existing.Open     = item.Open;
                existing.High     = item.High;
                existing.Low      = item.Low;
                existing.Close    = item.Close;
                existing.Volume   = item.Volume;
                existing.IsClosed = item.IsClosed;
            }
            else
            {
                await candleSet.AddAsync(new Domain.Entities.Candle
                {
                    Symbol    = symbol,
                    Timeframe = timeframe,
                    Open      = item.Open,
                    High      = item.High,
                    Low       = item.Low,
                    Close     = item.Close,
                    Volume    = item.Volume,
                    Timestamp = item.Timestamp,
                    IsClosed  = item.IsClosed,
                }, cancellationToken);
            }
            processed++;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return ResponseData<int>.Init(processed, true, "Successful", "00");
    }
}
