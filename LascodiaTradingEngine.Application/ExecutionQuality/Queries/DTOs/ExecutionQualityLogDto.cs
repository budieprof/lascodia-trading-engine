using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExecutionQuality.Queries.DTOs;

public class ExecutionQualityLogDto : IMapFrom<ExecutionQualityLog>
{
    public long           Id             { get; set; }
    public long           OrderId        { get; set; }
    public long?          StrategyId     { get; set; }
    public string?        Symbol         { get; set; }
    public TradingSession Session        { get; set; }
    public decimal  RequestedPrice { get; set; }
    public decimal  FilledPrice    { get; set; }
    public decimal  SlippagePips   { get; set; }
    public long     SubmitToFillMs { get; set; }
    public bool     WasPartialFill { get; set; }
    public decimal  FillRate       { get; set; }
    public DateTime RecordedAt     { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<ExecutionQualityLog, ExecutionQualityLogDto>();
    }
}
