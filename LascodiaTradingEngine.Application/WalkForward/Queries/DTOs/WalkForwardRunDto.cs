using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.WalkForward.Queries.DTOs;

public class WalkForwardRunDto : IMapFrom<WalkForwardRun>
{
    public long      Id                        { get; set; }
    public long      StrategyId                { get; set; }
    public string?   Symbol                    { get; set; }
    public Timeframe Timeframe                 { get; set; }
    public DateTime  FromDate                  { get; set; }
    public DateTime  ToDate                    { get; set; }
    public int       InSampleDays              { get; set; }
    public int       OutOfSampleDays           { get; set; }
    public RunStatus Status                    { get; set; }
    public decimal  InitialBalance            { get; set; }
    public decimal? AverageOutOfSampleScore   { get; set; }
    public decimal? ScoreConsistency          { get; set; }
    public string?  WindowResultsJson         { get; set; }
    public string?  ErrorMessage              { get; set; }
    public DateTime StartedAt                 { get; set; }
    public DateTime? CompletedAt              { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<WalkForwardRun, WalkForwardRunDto>();
    }
}
