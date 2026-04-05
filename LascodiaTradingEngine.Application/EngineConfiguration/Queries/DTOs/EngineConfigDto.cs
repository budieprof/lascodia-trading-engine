using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.EngineConfiguration.Queries.DTOs;

/// <summary>Data transfer object for an engine configuration key-value pair.</summary>
public class EngineConfigDto : IMapFrom<EngineConfig>
{
    public long           Id              { get; set; }
    public string?        Key             { get; set; }
    public string?        Value           { get; set; }
    public string?        Description     { get; set; }
    public ConfigDataType DataType        { get; set; }
    public bool           IsHotReloadable { get; set; }
    public DateTime       LastUpdatedAt   { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<EngineConfig, EngineConfigDto>();
    }
}
