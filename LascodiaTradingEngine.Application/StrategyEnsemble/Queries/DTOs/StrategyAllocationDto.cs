using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.StrategyEnsemble.Queries.DTOs;

/// <summary>Data transfer object for a strategy's ensemble allocation including weight and rolling Sharpe ratio.</summary>
public class StrategyAllocationDto : IMapFrom<StrategyAllocation>
{
    public long     Id                 { get; set; }
    public long     StrategyId         { get; set; }
    public string?  StrategyName       { get; set; }
    public decimal  Weight             { get; set; }
    public decimal  RollingSharpRatio  { get; set; }
    public DateTime LastRebalancedAt   { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<StrategyAllocation, StrategyAllocationDto>();
    }
}
