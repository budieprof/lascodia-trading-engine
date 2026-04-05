using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.RegisterEA;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Registers (or re-activates) a MetaTrader 5 Expert Advisor instance with the engine.
/// Validates symbol ownership to prevent overlapping coverage on the same trading account,
/// creates or updates the <see cref="Domain.Entities.EAInstance"/> record, and publishes
/// an <see cref="EAInstanceRegisteredIntegrationEvent"/> for downstream consumers.
/// Returns 409 if the requested symbols overlap with another active instance on the same account.
/// </summary>
public class RegisterEACommand : IRequest<ResponseData<long>>
{
    /// <summary>Unique identifier assigned to this EA instance (e.g. "EA-EURUSD-01"). Must be unique across all instances.</summary>
    public required string InstanceId       { get; set; }

    /// <summary>The trading account this EA instance belongs to. Used for ownership verification and symbol conflict checks.</summary>
    public long            TradingAccountId { get; set; }

    /// <summary>Comma-separated list of symbols this instance will stream data for (e.g. "EURUSD,GBPUSD"). Must not overlap with other active instances on the same account.</summary>
    public required string Symbols          { get; set; }

    /// <summary>The primary chart symbol the EA is attached to in MetaTrader 5.</summary>
    public required string ChartSymbol      { get; set; }

    /// <summary>Timeframe of the chart the EA is attached to (e.g. "H1", "M5"). Defaults to "H1".</summary>
    public string          ChartTimeframe   { get; set; } = "H1";

    /// <summary>Whether this instance acts as the coordinator for its trading account (handles cross-symbol commands like backfill requests).</summary>
    public bool            IsCoordinator    { get; set; }

    /// <summary>Semantic version of the EA software (e.g. "2.1.0"). Used for compatibility tracking.</summary>
    public string          EAVersion        { get; set; } = string.Empty;
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates EA registration requests. Enforces InstanceId non-empty (max 64 chars),
/// positive TradingAccountId, non-empty Symbols and ChartSymbol (max 10 chars), and EAVersion max 20 chars.
/// </summary>
public class RegisterEACommandValidator : AbstractValidator<RegisterEACommand>
{
    public RegisterEACommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty")
            .MaximumLength(64).WithMessage("InstanceId cannot exceed 64 characters");

        RuleFor(x => x.TradingAccountId)
            .GreaterThan(0).WithMessage("TradingAccountId must be greater than zero");

        RuleFor(x => x.Symbols)
            .NotEmpty().WithMessage("Symbols cannot be empty");

        RuleFor(x => x.ChartSymbol)
            .NotEmpty().WithMessage("ChartSymbol cannot be empty")
            .MaximumLength(10).WithMessage("ChartSymbol cannot exceed 10 characters");

        RuleFor(x => x.EAVersion)
            .MaximumLength(20).WithMessage("EAVersion cannot exceed 20 characters");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles EA instance registration. Verifies caller ownership of the trading account,
/// checks for symbol overlap with other active instances on the same account (returns 409 on conflict),
/// then creates or re-activates the EAInstance record and publishes an EAInstanceRegisteredIntegrationEvent.
/// </summary>
public class RegisterEACommandHandler : IRequestHandler<RegisterEACommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public RegisterEACommandHandler(
        IWriteApplicationDbContext context,
        IIntegrationEventService eventBus,
        IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _eventBus       = eventBus;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<long>> Handle(RegisterEACommand request, CancellationToken cancellationToken)
    {
        // Verify the caller owns the trading account they're registering the EA for
        var callerAccountId = _ownershipGuard.GetCallerAccountId();
        if (callerAccountId is null || callerAccountId != request.TradingAccountId)
            return ResponseData<long>.Init(0, false, "Unauthorized: caller does not own this trading account", "-403");

        var dbContext = _context.GetDbContext();
        var requestedSymbols = request.Symbols
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant())
            .ToHashSet();

        // ── Check for symbol overlap with other active instances on the same account ──
        var activeInstances = await dbContext
            .Set<Domain.Entities.EAInstance>()
            .Where(x => x.TradingAccountId == request.TradingAccountId
                      && x.Status == EAInstanceStatus.Active
                      && x.InstanceId != request.InstanceId
                      && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var active in activeInstances)
        {
            var existingSymbols = active.Symbols
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .ToHashSet();

            var overlap = requestedSymbols.Intersect(existingSymbols).ToList();
            if (overlap.Count > 0)
            {
                return ResponseData<long>.Init(
                    0, false,
                    $"Symbol overlap with instance '{active.InstanceId}': {string.Join(", ", overlap)}",
                    "-409");
            }
        }

        // ── Create or re-activate existing instance ──────────────────────────────
        var existing = await dbContext
            .Set<Domain.Entities.EAInstance>()
            .FirstOrDefaultAsync(
                x => x.InstanceId == request.InstanceId && !x.IsDeleted,
                cancellationToken);

        if (existing is not null)
        {
            existing.TradingAccountId = request.TradingAccountId;
            existing.Symbols          = string.Join(",", requestedSymbols);
            existing.ChartSymbol      = request.ChartSymbol.ToUpperInvariant();
            existing.ChartTimeframe   = request.ChartTimeframe;
            existing.IsCoordinator    = request.IsCoordinator;
            existing.EAVersion        = request.EAVersion;
            existing.Status           = EAInstanceStatus.Active;
            existing.LastHeartbeat    = DateTime.UtcNow;
            existing.DeregisteredAt   = null;

            await _eventBus.SaveAndPublish(_context, new EAInstanceRegisteredIntegrationEvent
            {
                EAInstanceId     = existing.Id,
                InstanceId       = existing.InstanceId,
                TradingAccountId = existing.TradingAccountId,
                Symbols          = existing.Symbols,
                IsCoordinator    = existing.IsCoordinator,
                RegisteredAt     = DateTime.UtcNow,
            });

            return ResponseData<long>.Init(existing.Id, true, "Re-registered", "00");
        }

        var entity = new Domain.Entities.EAInstance
        {
            InstanceId       = request.InstanceId,
            TradingAccountId = request.TradingAccountId,
            Symbols          = string.Join(",", requestedSymbols),
            ChartSymbol      = request.ChartSymbol.ToUpperInvariant(),
            ChartTimeframe   = request.ChartTimeframe,
            IsCoordinator    = request.IsCoordinator,
            EAVersion        = request.EAVersion,
            Status           = EAInstanceStatus.Active,
            LastHeartbeat    = DateTime.UtcNow,
            RegisteredAt     = DateTime.UtcNow,
        };

        await dbContext.Set<Domain.Entities.EAInstance>().AddAsync(entity, cancellationToken);

        await _eventBus.SaveAndPublish(_context, new EAInstanceRegisteredIntegrationEvent
        {
            EAInstanceId     = entity.Id,
            InstanceId       = entity.InstanceId,
            TradingAccountId = entity.TradingAccountId,
            Symbols          = entity.Symbols,
            IsCoordinator    = entity.IsCoordinator,
            RegisteredAt     = entity.RegisteredAt,
        });

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
