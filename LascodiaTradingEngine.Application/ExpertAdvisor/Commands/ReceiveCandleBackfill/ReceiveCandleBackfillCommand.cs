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
            .NotEmpty().WithMessage("Candles list cannot be empty");
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
            var exists = await dbContext
                .Set<Domain.Entities.Candle>()
                .AnyAsync(
                    x => x.Symbol == symbol
                      && x.Timeframe == timeframe
                      && x.Timestamp == candle.Timestamp
                      && !x.IsDeleted,
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
                Timestamp = candle.Timestamp,
                IsClosed  = true,
            }, cancellationToken);

            inserted++;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<int>.Init(inserted, true, $"Inserted {inserted} candles", "00");
    }
}
