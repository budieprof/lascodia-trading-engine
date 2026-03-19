using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TrailingStop.Commands.UpdateTrailingStop;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpdateTrailingStopCommand : IRequest<ResponseData<string>>
{
    public long    PositionId         { get; set; }
    public string  TrailingStopType   { get; set; } = string.Empty;  // "ATR" | "Fixed" | "Percentage"
    public decimal TrailingStopValue  { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpdateTrailingStopCommandValidator : AbstractValidator<UpdateTrailingStopCommand>
{
    public UpdateTrailingStopCommandValidator()
    {
        RuleFor(x => x.PositionId)
            .GreaterThan(0).WithMessage("PositionId must be greater than zero");

        RuleFor(x => x.TrailingStopType)
            .NotEmpty().WithMessage("TrailingStopType is required")
            .Must(t => Enum.TryParse<TrailingStopType>(t, ignoreCase: true, out _))
            .WithMessage("TrailingStopType must be 'FixedPips', 'ATR', or 'Percentage'");

        RuleFor(x => x.TrailingStopValue)
            .GreaterThan(0).WithMessage("TrailingStopValue must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpdateTrailingStopCommandHandler : IRequestHandler<UpdateTrailingStopCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateTrailingStopCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(UpdateTrailingStopCommand request, CancellationToken cancellationToken)
    {
        var position = await _context.GetDbContext()
            .Set<Domain.Entities.Position>()
            .FirstOrDefaultAsync(x => x.Id == request.PositionId && !x.IsDeleted, cancellationToken);

        if (position is null)
            return ResponseData<string>.Init(null, false, "Position not found", "-14");

        position.TrailingStopEnabled = true;
        position.TrailingStopType    = Enum.Parse<TrailingStopType>(request.TrailingStopType, ignoreCase: true);
        position.TrailingStopValue   = request.TrailingStopValue;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Successful", "00");
    }
}
