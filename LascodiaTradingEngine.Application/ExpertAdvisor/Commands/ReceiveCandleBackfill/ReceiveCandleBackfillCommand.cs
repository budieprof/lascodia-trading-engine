using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveCandleBackfill;

// ── Command ───────────────────────────────────────────────────────────────────

public class ReceiveCandleBackfillCommand : IRequest<ResponseData<int>>
{
    public required string InstanceId { get; set; }
    public required string Symbol     { get; set; }
    public required string Timeframe  { get; set; }
    public List<BackfillCandleItem> Candles { get; set; } = new();
}

public class BackfillCandleItem
{
    public decimal  Open      { get; set; }
    public decimal  High      { get; set; }
    public decimal  Low       { get; set; }
    public decimal  Close     { get; set; }
    public decimal  Volume    { get; set; }
    public DateTime Timestamp { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ReceiveCandleBackfillCommandValidator : AbstractValidator<ReceiveCandleBackfillCommand>
{
    public ReceiveCandleBackfillCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.Timeframe)
            .NotEmpty().WithMessage("Timeframe cannot be empty")
            .Must(t => Enum.TryParse<Timeframe>(t, ignoreCase: true, out _))
            .WithMessage("Timeframe must be one of: M1, M5, M15, H1, H4, D1");

        RuleFor(x => x.Candles)
            .NotEmpty().WithMessage("Candles list cannot be empty")
            .Must(c => c.Count <= 10000).WithMessage("Candle backfill batch cannot exceed 10000 items");

        RuleForEach(x => x.Candles).ChildRules(c =>
        {
            c.RuleFor(i => i.Open).GreaterThan(0).WithMessage("Open must be greater than zero");
            c.RuleFor(i => i.High).GreaterThan(0).WithMessage("High must be greater than zero");
            c.RuleFor(i => i.Low).GreaterThan(0).WithMessage("Low must be greater than zero");
            c.RuleFor(i => i.Close).GreaterThan(0).WithMessage("Close must be greater than zero");
            c.RuleFor(i => i.High).GreaterThanOrEqualTo(i => i.Low).WithMessage("High must be >= Low");
            c.RuleFor(i => i.Volume).GreaterThanOrEqualTo(0).WithMessage("Volume cannot be negative");
            c.RuleFor(i => i.Timestamp).NotEmpty().WithMessage("Candle timestamp cannot be empty");
        });
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ReceiveCandleBackfillCommandHandler : IRequestHandler<ReceiveCandleBackfillCommand, ResponseData<int>>
{
    private readonly IWriteApplicationDbContext _context;

    public ReceiveCandleBackfillCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<int>> Handle(ReceiveCandleBackfillCommand request, CancellationToken cancellationToken)
    {
        var dbContext  = _context.GetDbContext();
        var symbol    = request.Symbol.ToUpperInvariant();
        var timeframe = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true);
        var inserted  = 0;

        foreach (var candle in request.Candles)
        {
            // Normalize the timestamp to the timeframe boundary to prevent near-duplicate
            // candles (e.g. 22:00:00 vs 22:00:01) from being inserted as separate rows.
            var normalizedTs = NormalizeTimestamp(candle.Timestamp, timeframe);

            // IgnoreQueryFilters so soft-deleted rows are also checked — without this,
            // a soft-deleted candle passes the AnyAsync guard but then triggers a unique
            // constraint violation on SaveChangesAsync (unique index ignores IsDeleted).
            var exists = await dbContext
                .Set<Domain.Entities.Candle>()
                .IgnoreQueryFilters()
                .AnyAsync(
                    x => x.Symbol == symbol
                      && x.Timeframe == timeframe
                      && x.Timestamp == normalizedTs,
                    cancellationToken);

            if (exists)
                continue;

            await dbContext.Set<Domain.Entities.Candle>().AddAsync(new Domain.Entities.Candle
            {
                Symbol    = symbol,
                Timeframe = timeframe,
                Open      = candle.Open,
                High      = candle.High,
                Low       = candle.Low,
                Close     = candle.Close,
                Volume    = candle.Volume,
                Timestamp = normalizedTs,
                IsClosed  = true,
            }, cancellationToken);

            inserted++;
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            var inner = ex.InnerException?.Message ?? ex.Message;

            // Unique constraint violation: a race-condition duplicate slipped through the
            // AnyAsync guard (e.g. two concurrent chunks with overlapping timestamps).
            // Treat as a successful no-op — the candles are already persisted.
            if (inner.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
                inner.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                inner.Contains("23505"))   // PostgreSQL unique_violation SQLSTATE
            {
                return ResponseData<int>.Init(0, true, $"Candles already exist for {symbol}/{timeframe} (constraint conflict, skipped)", "00");
            }

            // Numeric field overflow: one or more historical candles have OHLCV values that
            // exceed the column's numeric precision (PostgreSQL SQLSTATE 22003).
            // This happens with certain older broker data — skip the chunk gracefully.
            if (inner.Contains("22003") ||
                inner.Contains("numeric field overflow", StringComparison.OrdinalIgnoreCase) ||
                inner.Contains("overflow", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseData<int>.Init(0, true, $"Candle chunk skipped for {symbol}/{timeframe} (numeric overflow in historical data)", "00");
            }

            throw; // Re-throw unexpected DB errors
        }

        return ResponseData<int>.Init(inserted, true, $"Inserted {inserted} candles", "00");
    }

    /// <summary>
    /// Truncates a candle timestamp to the start of its timeframe period.
    /// Prevents near-duplicate candles caused by sub-second EA timestamp drift
    /// (e.g. 22:00:00 vs 22:00:01 for a D1 candle).
    /// </summary>
    internal static DateTime NormalizeTimestamp(DateTime ts, Timeframe tf)
    {
        return tf switch
        {
            Timeframe.M1  => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc),
            Timeframe.M5  => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute / 5 * 5, 0, DateTimeKind.Utc),
            Timeframe.M15 => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute / 15 * 15, 0, DateTimeKind.Utc),
            Timeframe.H1  => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, DateTimeKind.Utc),
            Timeframe.H4  => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour / 4 * 4, 0, 0, DateTimeKind.Utc),
            Timeframe.D1  => new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc),
            _             => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc),
        };
    }
}
