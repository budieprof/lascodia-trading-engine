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

        // ── Rollback depth limit ─────────────────────────────────────────────
        // Read max depth from EngineConfig (default 3). Walk the PreviousChampionModelId
        // chain backwards from the current active model to count consecutive rollbacks.
        var maxDepthEntry = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == "MLModel:MaxRollbackDepth" && !c.IsDeleted, cancellationToken);
        int maxDepth = int.TryParse(maxDepthEntry?.Value, out var md) ? md : 3;

        int rollbackDepth = 0;
        long? walkId = current.PreviousChampionModelId;
        while (walkId.HasValue && rollbackDepth < maxDepth + 1)
        {
            var ancestor = await db.Set<MLModel>()
                .AsNoTracking()
                .Where(m => m.Id == walkId.Value && !m.IsDeleted)
                .Select(m => new { m.Id, m.Status, m.PreviousChampionModelId })
                .FirstOrDefaultAsync(cancellationToken);

            if (ancestor is null) break;

            // A superseded ancestor in the chain indicates it was previously rolled back from
            if (ancestor.Status == MLModelStatus.Superseded)
                rollbackDepth++;
            else
                break; // non-superseded ancestor means the chain of rollbacks ends

            walkId = ancestor.PreviousChampionModelId;
        }

        if (rollbackDepth >= maxDepth)
            return ResponseData<long>.Init(0, false,
                $"Rollback depth limit ({maxDepth}) reached — cannot roll back further. Manual intervention required.",
                "-11");

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
