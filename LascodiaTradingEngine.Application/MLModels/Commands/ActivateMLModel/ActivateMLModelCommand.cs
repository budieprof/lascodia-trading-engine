using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Commands.ActivateMLModel;

// ── Command ───────────────────────────────────────────────────────────────────

public class ActivateMLModelCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ActivateMLModelCommandValidator : AbstractValidator<ActivateMLModelCommand>
{
    public ActivateMLModelCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ActivateMLModelCommandHandler : IRequestHandler<ActivateMLModelCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IApprovalWorkflow _approvalWorkflow;
    private readonly ICurrentUserService _currentUser;

    public ActivateMLModelCommandHandler(
        IWriteApplicationDbContext context,
        IApprovalWorkflow approvalWorkflow,
        ICurrentUserService currentUser)
    {
        _context = context;
        _approvalWorkflow = approvalWorkflow;
        _currentUser = currentUser;
    }

    public async Task<ResponseData<string>> Handle(ActivateMLModelCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        var model = await db.Set<Domain.Entities.MLModel>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (model is null)
            return ResponseData<string>.Init(null, false, "ML model not found", "-14");

        // ── Four-eyes approval gate ──
        long currentAccountId = long.TryParse(_currentUser.UserId, out var parsedUid) ? parsedUid : 0;
        if (!await _approvalWorkflow.IsApprovedAsync(ApprovalOperationType.ModelPromotion, request.Id, cancellationToken))
        {
            await _approvalWorkflow.RequestApprovalAsync(
                ApprovalOperationType.ModelPromotion,
                request.Id,
                "MLModel",
                $"Activate ML model '{model.ModelVersion}' for {model.Symbol}/{model.Timeframe}",
                System.Text.Json.JsonSerializer.Serialize(new { request.Id }),
                currentAccountId,
                cancellationToken);
            return ResponseData<string>.Init(null, false, "Pending four-eyes approval", "-202");
        }

        if (!await _approvalWorkflow.ConsumeApprovalAsync(ApprovalOperationType.ModelPromotion, request.Id, cancellationToken))
            return ResponseData<string>.Init(null, false, "Approval was already consumed by a concurrent request", "-409");

        // Deactivate previously active models for the same Symbol + Timeframe
        var previousModels = await db.Set<Domain.Entities.MLModel>()
            .Where(x => x.Symbol == model.Symbol && x.Timeframe == model.Timeframe
                        && x.IsActive && x.Id != request.Id && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var prev in previousModels)
        {
            prev.IsActive = false;
            prev.Status   = MLModelStatus.Superseded;
        }

        model.IsActive    = true;
        model.Status      = MLModelStatus.Active;
        model.ActivatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Model activated successfully", true, "Successful", "00");
    }
}
