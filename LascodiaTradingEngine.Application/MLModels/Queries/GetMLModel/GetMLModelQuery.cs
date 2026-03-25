using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GetMLModel;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetMLModelQuery : IRequest<ResponseData<MLModelDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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
