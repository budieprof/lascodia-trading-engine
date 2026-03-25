using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Strategies.Commands.DeleteStrategy;

// ── Command ───────────────────────────────────────────────────────────────────

public class DeleteStrategyCommand : IRequest<ResponseData<string>>
{
    [JsonIgnore] public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class DeleteStrategyCommandValidator : AbstractValidator<DeleteStrategyCommand>
{
    public DeleteStrategyCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class DeleteStrategyCommandHandler : IRequestHandler<DeleteStrategyCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public DeleteStrategyCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(DeleteStrategyCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Strategy not found", "-14");

        entity.IsDeleted = true;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Deleted", true, "Successful", "00");
    }
}
