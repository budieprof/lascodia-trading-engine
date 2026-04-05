using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.RiskProfiles.Commands.CreateRiskProfile;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Creates a new risk profile defining position limits, drawdown thresholds, and recovery parameters.
/// If marked as default, the current default profile is unset first.
/// </summary>
public class CreateRiskProfileCommand : IRequest<ResponseData<long>>
{
    public required string Name                         { get; set; }
    public decimal         MaxLotSizePerTrade           { get; set; } = 1m;
    public decimal         MaxDailyDrawdownPct          { get; set; } = 2m;
    public decimal         MaxTotalDrawdownPct          { get; set; } = 10m;
    public int             MaxOpenPositions             { get; set; } = 5;
    public int             MaxDailyTrades               { get; set; } = 10;
    public decimal         MaxRiskPerTradePct           { get; set; } = 1m;
    public decimal         MaxSymbolExposurePct         { get; set; } = 5m;
    public bool            IsDefault                    { get; set; }
    public decimal         DrawdownRecoveryThresholdPct { get; set; } = 1.5m;
    public decimal         RecoveryLotSizeMultiplier    { get; set; } = 0.5m;
    public decimal         RecoveryExitThresholdPct     { get; set; } = 0.5m;
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class CreateRiskProfileCommandValidator : AbstractValidator<CreateRiskProfileCommand>
{
    public CreateRiskProfileCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty")
            .MaximumLength(100).WithMessage("Name cannot exceed 100 characters");

        RuleFor(x => x.MaxLotSizePerTrade)
            .GreaterThan(0).WithMessage("MaxLotSizePerTrade must be greater than zero");

        RuleFor(x => x.MaxDailyDrawdownPct)
            .GreaterThan(0).WithMessage("MaxDailyDrawdownPct must be greater than zero");

        RuleFor(x => x.MaxTotalDrawdownPct)
            .GreaterThan(0).WithMessage("MaxTotalDrawdownPct must be greater than zero");

        RuleFor(x => x.MaxOpenPositions)
            .GreaterThan(0).WithMessage("MaxOpenPositions must be greater than zero");

        RuleFor(x => x.MaxDailyTrades)
            .GreaterThan(0).WithMessage("MaxDailyTrades must be greater than zero");

        RuleFor(x => x.MaxRiskPerTradePct)
            .GreaterThan(0).WithMessage("MaxRiskPerTradePct must be greater than zero");

        RuleFor(x => x.MaxSymbolExposurePct)
            .GreaterThan(0).WithMessage("MaxSymbolExposurePct must be greater than zero");

        RuleFor(x => x.DrawdownRecoveryThresholdPct)
            .GreaterThan(0).WithMessage("DrawdownRecoveryThresholdPct must be greater than zero");

        RuleFor(x => x.RecoveryLotSizeMultiplier)
            .GreaterThan(0).WithMessage("RecoveryLotSizeMultiplier must be greater than zero");

        RuleFor(x => x.RecoveryExitThresholdPct)
            .GreaterThan(0).WithMessage("RecoveryExitThresholdPct must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Persists a new risk profile. If IsDefault is true, demotes the existing default profile first
/// to ensure only one default profile exists at a time.
/// </summary>
public class CreateRiskProfileCommandHandler : IRequestHandler<CreateRiskProfileCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public CreateRiskProfileCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(CreateRiskProfileCommand request, CancellationToken cancellationToken)
    {
        if (request.IsDefault)
        {
            var existingDefault = await _context.GetDbContext()
                .Set<Domain.Entities.RiskProfile>()
                .FirstOrDefaultAsync(x => x.IsDefault && !x.IsDeleted, cancellationToken);

            if (existingDefault != null)
                existingDefault.IsDefault = false;
        }

        var entity = new Domain.Entities.RiskProfile
        {
            Name                         = request.Name,
            MaxLotSizePerTrade           = request.MaxLotSizePerTrade,
            MaxDailyDrawdownPct          = request.MaxDailyDrawdownPct,
            MaxTotalDrawdownPct          = request.MaxTotalDrawdownPct,
            MaxOpenPositions             = request.MaxOpenPositions,
            MaxDailyTrades               = request.MaxDailyTrades,
            MaxRiskPerTradePct           = request.MaxRiskPerTradePct,
            MaxSymbolExposurePct         = request.MaxSymbolExposurePct,
            IsDefault                    = request.IsDefault,
            DrawdownRecoveryThresholdPct = request.DrawdownRecoveryThresholdPct,
            RecoveryLotSizeMultiplier    = request.RecoveryLotSizeMultiplier,
            RecoveryExitThresholdPct     = request.RecoveryExitThresholdPct
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.RiskProfile>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
