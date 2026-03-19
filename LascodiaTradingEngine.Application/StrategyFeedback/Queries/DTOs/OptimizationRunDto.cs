using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Queries.DTOs;

public class OptimizationRunDto : IMapFrom<OptimizationRun>
{
    public long                  Id                     { get; set; }
    public long                  StrategyId             { get; set; }
    public TriggerType           TriggerType            { get; set; }
    public OptimizationRunStatus Status                 { get; set; }
    public int       Iterations             { get; set; }
    public string?   BestParametersJson     { get; set; }
    public decimal?  BestHealthScore        { get; set; }
    public string?   BaselineParametersJson { get; set; }
    public decimal?  BaselineHealthScore    { get; set; }
    public string?   ErrorMessage           { get; set; }
    public DateTime  StartedAt              { get; set; }
    public DateTime? CompletedAt            { get; set; }
    public DateTime? ApprovedAt             { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<OptimizationRun, OptimizationRunDto>();
    }
}
