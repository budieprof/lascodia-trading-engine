using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.EngineConfiguration.Commands.UpsertEngineConfig;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpsertEngineConfigCommand : IRequest<ResponseData<long>>
{
    public string  Key              { get; set; } = string.Empty;
    public string  Value            { get; set; } = string.Empty;
    public string? Description      { get; set; }
    public string  DataType         { get; set; } = "String";
    public bool    IsHotReloadable  { get; set; } = true;
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpsertEngineConfigCommandValidator : AbstractValidator<UpsertEngineConfigCommand>
{
    public UpsertEngineConfigCommandValidator()
    {
        RuleFor(x => x.Key)
            .NotEmpty().WithMessage("Key is required");

        RuleFor(x => x.Value)
            .NotEmpty().WithMessage("Value is required");

        RuleFor(x => x.DataType)
            .NotEmpty().WithMessage("DataType is required")
            .Must(t => Enum.TryParse<ConfigDataType>(t, ignoreCase: true, out _))
            .WithMessage("DataType must be 'String', 'Int', 'Decimal', 'Bool', or 'Json'");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpsertEngineConfigCommandHandler : IRequestHandler<UpsertEngineConfigCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpsertEngineConfigCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(UpsertEngineConfigCommand request, CancellationToken cancellationToken)
    {
        var existing = await _context.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .FirstOrDefaultAsync(x => x.Key == request.Key && !x.IsDeleted, cancellationToken);

        var dataType = Enum.Parse<ConfigDataType>(request.DataType, ignoreCase: true);

        if (existing is not null)
        {
            existing.Value           = request.Value;
            existing.Description     = request.Description;
            existing.DataType        = dataType;
            existing.IsHotReloadable = request.IsHotReloadable;
            existing.LastUpdatedAt   = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            return ResponseData<long>.Init(existing.Id, true, "Updated", "00");
        }

        var entity = new Domain.Entities.EngineConfig
        {
            Key             = request.Key,
            Value           = request.Value,
            Description     = request.Description,
            DataType        = dataType,
            IsHotReloadable = request.IsHotReloadable,
            LastUpdatedAt   = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Created", "00");
    }
}
