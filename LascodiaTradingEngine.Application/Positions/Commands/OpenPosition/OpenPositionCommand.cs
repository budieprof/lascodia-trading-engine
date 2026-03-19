using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Positions.Commands.OpenPosition;

// ── Command ───────────────────────────────────────────────────────────────────

public class OpenPositionCommand : IRequest<ResponseData<long>>
{
    public required string Symbol            { get; set; }
    public required string Direction         { get; set; }  // "Long" | "Short"
    public decimal         OpenLots          { get; set; }
    public decimal         AverageEntryPrice { get; set; }
    public decimal?        StopLoss          { get; set; }
    public decimal?        TakeProfit        { get; set; }
    public bool            IsPaper           { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class OpenPositionCommandValidator : AbstractValidator<OpenPositionCommand>
{
    public OpenPositionCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.Direction)
            .NotEmpty().WithMessage("Direction cannot be empty")
            .Must(d => Enum.TryParse<PositionDirection>(d, ignoreCase: true, out _)).WithMessage("Direction must be 'Long' or 'Short'");

        RuleFor(x => x.OpenLots)
            .GreaterThan(0).WithMessage("OpenLots must be greater than zero");

        RuleFor(x => x.AverageEntryPrice)
            .GreaterThan(0).WithMessage("AverageEntryPrice must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class OpenPositionCommandHandler : IRequestHandler<OpenPositionCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public OpenPositionCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(OpenPositionCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.Position
        {
            Symbol            = request.Symbol,
            Direction         = Enum.Parse<PositionDirection>(request.Direction, ignoreCase: true),
            OpenLots          = request.OpenLots,
            AverageEntryPrice = request.AverageEntryPrice,
            StopLoss          = request.StopLoss,
            TakeProfit        = request.TakeProfit,
            IsPaper           = request.IsPaper,
            Status            = PositionStatus.Open,
            OpenedAt          = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.Position>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
