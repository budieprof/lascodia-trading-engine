using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.PaperTrading.Commands.SetPaperTradingMode;

// ── Command ───────────────────────────────────────────────────────────────────

public class SetPaperTradingModeCommand : IRequest<ResponseData<string>>
{
    public bool    IsPaperMode { get; set; }
    public string? Reason      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class SetPaperTradingModeCommandValidator : AbstractValidator<SetPaperTradingModeCommand>
{
    public SetPaperTradingModeCommandValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(500).When(x => x.Reason is not null);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class SetPaperTradingModeCommandHandler
    : IRequestHandler<SetPaperTradingModeCommand, ResponseData<string>>
{
    private const string ConfigKey = "Engine:PaperMode";

    private readonly IWriteApplicationDbContext _context;

    public SetPaperTradingModeCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(
        SetPaperTradingModeCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        // Upsert EngineConfig
        var config = await db.Set<Domain.Entities.EngineConfig>()
            .FirstOrDefaultAsync(x => x.Key == ConfigKey && !x.IsDeleted, cancellationToken);

        if (config == null)
        {
            config = new Domain.Entities.EngineConfig
            {
                Key             = ConfigKey,
                DataType        = ConfigDataType.Bool,
                Description     = "Whether the engine is running in paper (simulation) mode",
                IsHotReloadable = true
            };
            await db.Set<Domain.Entities.EngineConfig>().AddAsync(config, cancellationToken);
        }

        config.Value         = request.IsPaperMode.ToString().ToLower();
        config.LastUpdatedAt = DateTime.UtcNow;

        // Write audit DecisionLog
        var decisionLog = new Domain.Entities.DecisionLog
        {
            EntityType   = "EngineConfig",
            EntityId     = config.Id,
            DecisionType = "PaperModeChanged",
            Outcome      = request.IsPaperMode ? "PaperMode" : "LiveMode",
            Reason       = request.Reason ?? (request.IsPaperMode
                ? "Switched to paper trading mode"
                : "Switched to live trading mode"),
            Source    = "PaperTradingController",
            CreatedAt = DateTime.UtcNow
        };

        await db.Set<Domain.Entities.DecisionLog>().AddAsync(decisionLog, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        var modeLabel = request.IsPaperMode ? "paper" : "live";
        return ResponseData<string>.Init($"Engine switched to {modeLabel} trading mode", true, "Successful", "00");
    }
}
