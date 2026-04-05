using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Queries.DTOs;

/// <summary>Read-only projection of the <see cref="Strategy"/> entity for API responses and query results.</summary>
public class StrategyDto : IMapFrom<Strategy>
{
    /// <summary>Unique strategy identifier.</summary>
    public long         Id             { get; set; }
    /// <summary>Human-readable strategy name.</summary>
    public string?      Name           { get; set; }
    /// <summary>Detailed description of the strategy logic.</summary>
    public string?      Description    { get; set; }
    /// <summary>Strategy algorithm type.</summary>
    public StrategyType StrategyType   { get; set; }
    /// <summary>Currency pair symbol the strategy trades.</summary>
    public string?      Symbol         { get; set; }
    /// <summary>Candle timeframe used for evaluation.</summary>
    public Timeframe    Timeframe      { get; set; }
    /// <summary>JSON-serialized strategy-specific parameters.</summary>
    public string?      ParametersJson { get; set; }
    /// <summary>Current strategy lifecycle status (Active/Paused/etc.).</summary>
    public StrategyStatus Status       { get; set; }
    /// <summary>Assigned risk profile identifier, if any.</summary>
    public long?        RiskProfileId  { get; set; }
    /// <summary>Timestamp when the strategy was created.</summary>
    public DateTime     CreatedAt      { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Strategy, StrategyDto>();
    }
}
