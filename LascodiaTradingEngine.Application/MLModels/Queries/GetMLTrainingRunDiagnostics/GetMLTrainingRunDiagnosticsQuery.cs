using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GetMLTrainingRunDiagnostics;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves the full diagnostic record for a single ML training run: advanced
/// evaluation metrics (F1, Brier, Sharpe, abstention), dataset stats, hyperparameter
/// JSON, drift trigger metadata, and the feature-flag audit trail of training-time
/// techniques that were applied (SMOTE, mixup, curriculum, MAML, pruning, etc.).
///
/// Returns <c>-14</c> if the run does not exist.
/// </summary>
public class GetMLTrainingRunDiagnosticsQuery : IRequest<ResponseData<MLTrainingRunDiagnosticsDto>>
{
    /// <summary>Database ID of the training run to retrieve diagnostics for.</summary>
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Loads the full <see cref="Domain.Entities.MLTrainingRun"/> row and maps it onto
/// <see cref="MLTrainingRunDiagnosticsDto"/>. Soft-deleted rows are excluded.
/// </summary>
public class GetMLTrainingRunDiagnosticsQueryHandler
    : IRequestHandler<GetMLTrainingRunDiagnosticsQuery, ResponseData<MLTrainingRunDiagnosticsDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetMLTrainingRunDiagnosticsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<MLTrainingRunDiagnosticsDto>> Handle(
        GetMLTrainingRunDiagnosticsQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.MLTrainingRun>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<MLTrainingRunDiagnosticsDto>.Init(
                null, false, "ML training run not found", "-14");

        return ResponseData<MLTrainingRunDiagnosticsDto>.Init(
            _mapper.Map<MLTrainingRunDiagnosticsDto>(entity), true, "Successful", "00");
    }
}
