using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GetMLModel;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves a single ML model by its database ID. Returns -14 if not found.
/// </summary>
public class GetMLModelQuery : IRequest<ResponseData<MLModelDto>>
{
    /// <summary>Database ID of the ML model to retrieve.</summary>
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles single ML model retrieval by ID, mapping the entity to MLModelDto.
/// </summary>
public class GetMLModelQueryHandler : IRequestHandler<GetMLModelQuery, ResponseData<MLModelDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetMLModelQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<MLModelDto>> Handle(GetMLModelQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<MLModelDto>.Init(null, false, "ML model not found", "-14");

        return ResponseData<MLModelDto>.Init(_mapper.Map<MLModelDto>(entity), true, "Successful", "00");
    }
}
