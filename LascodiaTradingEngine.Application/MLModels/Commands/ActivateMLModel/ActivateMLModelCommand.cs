using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Commands.ActivateMLModel;

// ── Command ───────────────────────────────────────────────────────────────────

public class ActivateMLModelCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ActivateMLModelCommandHandler : IRequestHandler<ActivateMLModelCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public ActivateMLModelCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ActivateMLModelCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        var model = await db.Set<Domain.Entities.MLModel>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (model is null)
            return ResponseData<string>.Init(null, false, "ML model not found", "-14");

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
