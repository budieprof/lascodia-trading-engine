using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveCandle;

// ── Command ───────────────────────────────────────────────────────────────────

public class ReceiveCandleCommand : IRequest<ResponseData<long>>
{
    public required string InstanceId { get; set; }
    public required string Symbol     { get; set; }
    public required string Timeframe  { get; set; }
    public decimal  Open      { get; set; }
    public decimal  High      { get; set; }
    public decimal  Low       { get; set; }
    public decimal  Close     { get; set; }
    public decimal  Volume    { get; set; }
    public DateTime Timestamp { get; set; }
    public bool     IsClosed  { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ReceiveCandleCommandValidator : AbstractValidator<ReceiveCandleCommand>
{
    public ReceiveCandleCommandValidator()
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

        RuleFor(x => x.Open).GreaterThan(0).WithMessage("Open must be greater than zero");
        RuleFor(x => x.High).GreaterThan(0).WithMessage("High must be greater than zero");
        RuleFor(x => x.Low).GreaterThan(0).WithMessage("Low must be greater than zero");
        RuleFor(x => x.Close).GreaterThan(0).WithMessage("Close must be greater than zero");
        RuleFor(x => x.Volume).GreaterThanOrEqualTo(0).WithMessage("Volume cannot be negative");
        RuleFor(x => x.Timestamp).NotEmpty().WithMessage("Timestamp cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ReceiveCandleCommandHandler : IRequestHandler<ReceiveCandleCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public ReceiveCandleCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(ReceiveCandleCommand request, CancellationToken cancellationToken)
    {
        var dbContext  = _context.GetDbContext();
        var symbol    = request.Symbol.ToUpperInvariant();
        var timeframe = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true);

        // Normalize timestamp to the timeframe boundary to prevent near-duplicate
        // candles caused by sub-second EA timestamp drift.
        var normalizedTs = ReceiveCandleBackfill.ReceiveCandleBackfillCommandHandler
            .NormalizeTimestamp(request.Timestamp, timeframe);

        var existing = await dbContext
            .Set<Domain.Entities.Candle>()
            .FirstOrDefaultAsync(
                x => x.Symbol == symbol
                  && x.Timeframe == timeframe
                  && x.Timestamp == normalizedTs
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
            Symbol    = symbol,
            Timeframe = timeframe,
            Open      = request.Open,
            High      = request.High,
            Low       = request.Low,
            Close     = request.Close,
            Volume    = request.Volume,
            Timestamp = normalizedTs,
            IsClosed  = request.IsClosed,
        };

        await dbContext.Set<Domain.Entities.Candle>().AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
