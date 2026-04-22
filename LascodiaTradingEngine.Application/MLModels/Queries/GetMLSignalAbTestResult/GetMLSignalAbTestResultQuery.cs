using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GetMLSignalAbTestResult;

/// <summary>
/// Retrieves a single terminal signal-level A/B test result by ID.
/// </summary>
public class GetMLSignalAbTestResultQuery : IRequest<ResponseData<MLSignalAbTestResultDto>>
{
    public required long Id { get; set; }
}

public class GetMLSignalAbTestResultQueryHandler
    : IRequestHandler<GetMLSignalAbTestResultQuery, ResponseData<MLSignalAbTestResultDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetMLSignalAbTestResultQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<ResponseData<MLSignalAbTestResultDto>> Handle(
        GetMLSignalAbTestResultQuery request,
        CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.MLSignalAbTestResult>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<MLSignalAbTestResultDto>.Init(null, false, "Signal A/B test result not found", "-14");

        return ResponseData<MLSignalAbTestResultDto>.Init(
            _mapper.Map<MLSignalAbTestResultDto>(entity),
            true,
            "Successful",
            "00");
    }
}
