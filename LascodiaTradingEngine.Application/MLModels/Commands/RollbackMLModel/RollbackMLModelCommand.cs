using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Commands.RollbackMLModel;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Rolls back to the most recently superseded model for a given symbol/timeframe.
/// Demotes the current active model to <see cref="MLModelStatus.Superseded"/> and
/// re-activates the previous champion, giving operators a one-click escape hatch
/// when a newly promoted model performs poorly in production.
/// </summary>
public class RollbackMLModelCommand : IRequest<ResponseData<long>>
{
    /// <summary>Currency pair to roll back (e.g. "EURUSD").</summary>
    public required string Symbol    { get; set; }

    /// <summary>Chart timeframe to roll back.</summary>
    public required string Timeframe { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RollbackMLModelCommandValidator : AbstractValidator<RollbackMLModelCommand>
{
    public RollbackMLModelCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol is required")
            .MaximumLength(10);

        RuleFor(x => x.Timeframe)
            .NotEmpty().WithMessage("Timeframe is required");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class RollbackMLModelCommandHandler
    : IRequestHandler<RollbackMLModelCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public RollbackMLModelCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(
        RollbackMLModelCommand request,
        CancellationToken      cancellationToken)
    {
        if (!Enum.TryParse<Timeframe>(request.Timeframe, ignoreCase: true, out var tf))
            return ResponseData<long>.Init(0, false, $"Unknown timeframe: {request.Timeframe}", "-11");

        var db = _context.GetDbContext();

        // Find the current active champion
        var current = await db.Set<MLModel>()
            .FirstOrDefaultAsync(
                m => m.Symbol == request.Symbol && m.Timeframe == tf && m.IsActive && !m.IsDeleted,
                cancellationToken);

        if (current is null)
            return ResponseData<long>.Init(0, false,
                "No active model found for the given symbol/timeframe.", "-14");

        // Find the most recently superseded model (previous champion by ActivatedAt)
        var previous = await db.Set<MLModel>()
            .Where(m => m.Symbol    == request.Symbol &&
                        m.Timeframe == tf             &&
                        m.Status    == MLModelStatus.Superseded &&
                        !m.IsDeleted)
            .OrderByDescending(m => m.ActivatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (previous is null)
            return ResponseData<long>.Init(0, false,
                "No superseded model available to roll back to.", "-14");

        // Demote current champion
        current.IsActive = false;
        current.Status   = MLModelStatus.Superseded;

        // Reinstate previous champion
        previous.IsActive    = true;
        previous.Status      = MLModelStatus.Active;
        previous.ActivatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(
            previous.Id,
            true,
            $"Rolled back from model {current.Id} to model {previous.Id} " +
            $"({request.Symbol}/{tf}) successfully.",
            "00");
    }
}
