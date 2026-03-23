using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveCandleBatch;

// ── DTO ──────────────────────────────────────────────────────────────────────

public class CandleBatchItem
{
    public required string Symbol    { get; set; }
    public required string Timeframe { get; set; }
    public decimal  Open      { get; set; }
    public decimal  High      { get; set; }
    public decimal  Low       { get; set; }
    public decimal  Close     { get; set; }
    public decimal  Volume    { get; set; }
    public DateTime Timestamp { get; set; }
    public bool     IsClosed  { get; set; }
}

// ── Command ──────────────────────────────────────────────────────────────────

public class ReceiveCandleBatchCommand : IRequest<ResponseData<int>>
{
    public required string InstanceId { get; set; }
    public List<CandleBatchItem> Candles { get; set; } = new();
}

// ── Validator ────────────────────────────────────────────────────────────────

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
