using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Queries.DTOs;

public class StrategyDto : IMapFrom<Strategy>
{
    public long         Id             { get; set; }
    public string?      Name           { get; set; }
    public string?      Description    { get; set; }
    public StrategyType StrategyType   { get; set; }
    public string?      Symbol         { get; set; }
    public Timeframe    Timeframe      { get; set; }
    public string?      ParametersJson { get; set; }
    public StrategyStatus Status       { get; set; }
    public long?        RiskProfileId  { get; set; }
    public DateTime     CreatedAt      { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Strategy, StrategyDto>();
    }
}
