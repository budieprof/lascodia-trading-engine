using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GetMLTrainingRun;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves a single ML training run by its database ID. Returns -14 if not found.
/// </summary>
public class GetMLTrainingRunQuery : IRequest<ResponseData<MLTrainingRunDto>>
{
    /// <summary>Database ID of the training run to retrieve.</summary>
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles single ML training run retrieval by ID, mapping the entity to MLTrainingRunDto.
/// </summary>
public class GetMLTrainingRunQueryHandler : IRequestHandler<GetMLTrainingRunQuery, ResponseData<MLTrainingRunDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetMLTrainingRunQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<MLTrainingRunDto>> Handle(GetMLTrainingRunQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.MLTrainingRun>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<MLTrainingRunDto>.Init(null, false, "ML training run not found", "-14");

        return ResponseData<MLTrainingRunDto>.Init(_mapper.Map<MLTrainingRunDto>(entity), true, "Successful", "00");
    }
}
