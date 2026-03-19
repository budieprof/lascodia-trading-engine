using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Queries.DTOs;

public class StrategyPerformanceSnapshotDto : IMapFrom<StrategyPerformanceSnapshot>
{
    public long     Id             { get; set; }
    public long     StrategyId     { get; set; }
    public int      WindowTrades   { get; set; }
    public int      WinningTrades  { get; set; }
    public int      LosingTrades   { get; set; }
    public decimal  WinRate        { get; set; }
    public decimal  ProfitFactor   { get; set; }
    public decimal  SharpeRatio    { get; set; }
    public decimal  MaxDrawdownPct { get; set; }
    public decimal  TotalPnL       { get; set; }
    public decimal  HealthScore    { get; set; }
    public string?  HealthStatus   { get; set; }
    public DateTime EvaluatedAt    { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<StrategyPerformanceSnapshot, StrategyPerformanceSnapshotDto>();
    }
}
