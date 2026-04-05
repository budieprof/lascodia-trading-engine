using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Queries.DTOs;

/// <summary>
/// Data transfer object for a registered EA instance, representing a MetaTrader 5 Expert Advisor
/// connected to the engine.
/// </summary>
public class EAInstanceDto : IMapFrom<EAInstance>
{
    /// <summary>Database ID of the EA instance record.</summary>
    public long              Id               { get; set; }

    /// <summary>Unique string identifier assigned to this EA instance.</summary>
    public string            InstanceId       { get; set; } = string.Empty;

    /// <summary>ID of the trading account this instance is linked to.</summary>
    public long              TradingAccountId { get; set; }

    /// <summary>Comma-separated list of symbols this instance streams data for.</summary>
    public string            Symbols          { get; set; } = string.Empty;

    /// <summary>Primary chart symbol the EA is attached to in MT5.</summary>
    public string            ChartSymbol      { get; set; } = string.Empty;

    /// <summary>Chart timeframe (e.g. "H1", "M5").</summary>
    public string            ChartTimeframe   { get; set; } = string.Empty;

    /// <summary>Whether this instance is the coordinator for its trading account.</summary>
    public bool              IsCoordinator    { get; set; }

    /// <summary>Current instance status: Active, Disconnected, or ShuttingDown.</summary>
    public EAInstanceStatus  Status           { get; set; }

    /// <summary>UTC time of the most recent heartbeat or tick received from this instance.</summary>
    public DateTime          LastHeartbeat    { get; set; }

    /// <summary>Semantic version of the EA software.</summary>
    public string            EAVersion        { get; set; } = string.Empty;

    /// <summary>UTC time when the instance was first registered.</summary>
    public DateTime          RegisteredAt     { get; set; }

    /// <summary>UTC time when the instance was deregistered, if applicable.</summary>
    public DateTime?         DeregisteredAt   { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<EAInstance, EAInstanceDto>();
    }
}
