using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Queries.DTOs;

public class EAInstanceDto : IMapFrom<EAInstance>
{
    public long              Id               { get; set; }
    public string            InstanceId       { get; set; } = string.Empty;
    public long              TradingAccountId { get; set; }
    public string            Symbols          { get; set; } = string.Empty;
    public string            ChartSymbol      { get; set; } = string.Empty;
    public string            ChartTimeframe   { get; set; } = string.Empty;
    public bool              IsCoordinator    { get; set; }
    public EAInstanceStatus  Status           { get; set; }
    public DateTime          LastHeartbeat    { get; set; }
    public string            EAVersion        { get; set; } = string.Empty;
    public DateTime          RegisteredAt     { get; set; }
    public DateTime?         DeregisteredAt   { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<EAInstance, EAInstanceDto>();
    }
}
