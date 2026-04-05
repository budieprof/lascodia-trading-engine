using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MarketData.Commands.IngestCandle;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Ingests a single OHLCV candle into the Candle table. Performs an upsert by
/// (Symbol, Timeframe, Timestamp) -- updates the existing candle if found, otherwise inserts a new one.
/// This is the general-purpose candle ingestion command; for EA-specific ingestion with anomaly
/// detection and timestamp normalisation, use ReceiveCandleCommand.
/// </summary>
public class IngestCandleCommand : IRequest<ResponseData<long>>
{
    /// <summary>Instrument symbol (e.g. "EURUSD").</summary>
    public required string Symbol    { get; set; }

    /// <summary>Bar timeframe as a string (e.g. "M1", "H1", "D1"). Must parse to the Timeframe enum.</summary>
    public required string Timeframe { get; set; }

    /// <summary>Opening price of the candle.</summary>
    public decimal  Open      { get; set; }

    /// <summary>Highest price during the candle period.</summary>
    public decimal  High      { get; set; }

    /// <summary>Lowest price during the candle period.</summary>
    public decimal  Low       { get; set; }

    /// <summary>Closing (or current) price of the candle.</summary>
    public decimal  Close     { get; set; }

    /// <summary>Tick volume for the candle period. Zero is valid for illiquid sessions.</summary>
    public decimal  Volume    { get; set; }

    /// <summary>Candle open time.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>True if the bar is complete; false if still forming.</summary>
    public bool     IsClosed  { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates candle ingestion: non-empty Symbol (max 10 chars), valid Timeframe enum,
/// positive OHLC values, non-negative Volume, and non-default Timestamp.
/// </summary>
public class IngestCandleCommandValidator : AbstractValidator<IngestCandleCommand>
{
    public IngestCandleCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.Timeframe)
            .NotEmpty().WithMessage("Timeframe cannot be empty")
            .Must(t => Enum.TryParse<Timeframe>(t, ignoreCase: true, out _))
            .WithMessage("Timeframe must be one of: M1, M5, M15, H1, H4, D1");

        RuleFor(x => x.Open).GreaterThan(0).WithMessage("Open must be greater than zero");
        RuleFor(x => x.High).GreaterThan(0).WithMessage("High must be greater than zero");
        RuleFor(x => x.Low).GreaterThan(0).WithMessage("Low must be greater than zero");
        RuleFor(x => x.Close).GreaterThan(0).WithMessage("Close must be greater than zero");
        RuleFor(x => x.Volume).GreaterThanOrEqualTo(0).WithMessage("Volume cannot be negative");
        RuleFor(x => x.Timestamp).NotEmpty().WithMessage("Timestamp cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles candle ingestion by upserting into the Candle table. Looks up an existing candle by
/// (Symbol, Timeframe, Timestamp); if found, overwrites OHLCV and IsClosed; otherwise inserts a new row.
/// Returns the candle entity ID on success.
/// </summary>
public class IngestCandleCommandHandler : IRequestHandler<IngestCandleCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public IngestCandleCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(IngestCandleCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();

        // If a candle for this symbol/timeframe/timestamp already exists, upsert it
        var timeframe = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true);

        var existing = await dbContext
            .Set<Domain.Entities.Candle>()
            .FirstOrDefaultAsync(
                x => x.Symbol == request.Symbol
                  && x.Timeframe == timeframe
                  && x.Timestamp == request.Timestamp
                  && !x.IsDeleted,
                cancellationToken);

        if (existing is not null)
        {
            existing.Open     = request.Open;
            existing.High     = request.High;
            existing.Low      = request.Low;
            existing.Close    = request.Close;
            existing.Volume   = request.Volume;
            existing.IsClosed = request.IsClosed;

            await _context.SaveChangesAsync(cancellationToken);
            return ResponseData<long>.Init(existing.Id, true, "Updated", "00");
        }

        var entity = new Domain.Entities.Candle
        {
            Symbol    = request.Symbol.ToUpperInvariant(),
            Timeframe = timeframe,
            Open      = request.Open,
            High      = request.High,
            Low       = request.Low,
            Close     = request.Close,
            Volume    = request.Volume,
            Timestamp = request.Timestamp,
            IsClosed  = request.IsClosed
        };

        await dbContext.Set<Domain.Entities.Candle>().AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
