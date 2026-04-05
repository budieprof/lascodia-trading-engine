using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.RefreshSymbolSpecs;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queues a RequestBackfill command to the coordinator EA instance so it re-sends
/// symbol specifications for all watched symbols.
/// </summary>
public class RefreshSymbolSpecsCommand : IRequest<ResponseData<string>>
{
    /// <summary>Trading account ID whose coordinator EA instance should refresh symbol specs.</summary>
    public long TradingAccountId { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates that TradingAccountId is a positive number.
/// </summary>
public class RefreshSymbolSpecsCommandValidator : AbstractValidator<RefreshSymbolSpecsCommand>
{
    public RefreshSymbolSpecsCommandValidator()
    {
        RuleFor(x => x.TradingAccountId)
            .GreaterThan(0).WithMessage("TradingAccountId must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles symbol spec refresh by locating the coordinator EA instance for the given trading account
/// and queuing a RequestBackfill command with action "refreshSymbolSpecs". Returns -14 if no active
/// coordinator is found.
/// </summary>
public class RefreshSymbolSpecsCommandHandler : IRequestHandler<RefreshSymbolSpecsCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public RefreshSymbolSpecsCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(RefreshSymbolSpecsCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();

        // Find the coordinator instance for this trading account
        var coordinator = await dbContext
            .Set<Domain.Entities.EAInstance>()
            .FirstOrDefaultAsync(
                x => x.TradingAccountId == request.TradingAccountId
                  && x.IsCoordinator
                  && x.Status == EAInstanceStatus.Active
                  && !x.IsDeleted,
                cancellationToken);

        if (coordinator == null)
            return ResponseData<string>.Init(null, false, "No active coordinator found for this trading account", "-14");

        // Queue a command for the coordinator to re-send symbol specs
        await dbContext.Set<Domain.Entities.EACommand>().AddAsync(new Domain.Entities.EACommand
        {
            TargetInstanceId = coordinator.InstanceId,
            CommandType      = EACommandType.RequestBackfill,
            Symbol           = "*",
            Parameters       = "{\"action\":\"refreshSymbolSpecs\"}",
        }, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Refresh queued", "00");
    }
}
