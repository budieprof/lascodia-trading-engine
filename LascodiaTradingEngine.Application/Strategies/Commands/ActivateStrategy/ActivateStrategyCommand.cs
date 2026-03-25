using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Commands.ActivateStrategy;

// ── Command ───────────────────────────────────────────────────────────────────

public class ActivateStrategyCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ActivateStrategyCommandValidator : AbstractValidator<ActivateStrategyCommand>
{
    public ActivateStrategyCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ActivateStrategyCommandHandler : IRequestHandler<ActivateStrategyCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;

    public ActivateStrategyCommandHandler(IWriteApplicationDbContext context, IIntegrationEventService eventBus)
    {
        _context  = context;
        _eventBus = eventBus;
    }

    public async Task<ResponseData<string>> Handle(ActivateStrategyCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Strategy not found", "-14");

        entity.Status = StrategyStatus.Active;

        // ── Auto-queue an initial BacktestRun so the strategy has a performance baseline ──
        var toDate   = DateTime.UtcNow;
        var fromDate = toDate.AddYears(-1);

        var backtestRun = new BacktestRun
        {
            StrategyId     = entity.Id,
            Symbol         = entity.Symbol,
            Timeframe      = entity.Timeframe,
            FromDate       = fromDate,
            ToDate         = toDate,
            InitialBalance = 10_000m,
            Status         = RunStatus.Queued,
            StartedAt      = DateTime.UtcNow
        };

        await _context.GetDbContext().Set<BacktestRun>().AddAsync(backtestRun, cancellationToken);

        // ── Publish StrategyActivatedIntegrationEvent ─────────────────────────
        await _eventBus.SaveAndPublish(_context, new StrategyActivatedIntegrationEvent
        {
            StrategyId  = entity.Id,
            Name        = entity.Name,
            Symbol      = entity.Symbol,
            Timeframe   = entity.Timeframe,
            ActivatedAt = DateTime.UtcNow
        });

        return ResponseData<string>.Init("Activated", true, "Successful", "00");
    }
}
