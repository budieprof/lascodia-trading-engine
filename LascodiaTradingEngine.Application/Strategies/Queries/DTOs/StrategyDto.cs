using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Application.StrategyGeneration;
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
    /// <summary>Parsed strategy-generation screening metadata when available.</summary>
    public StrategyScreeningMetadataDto? ScreeningMetadata { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Strategy, StrategyDto>()
            .ForMember(
                dest => dest.ScreeningMetadata,
                opt => opt.MapFrom(src => StrategyScreeningMetadataDto.FromJson(src.ScreeningMetricsJson)));
    }
}

/// <summary>Compact screening metadata projection for strategy list/read models.</summary>
public class StrategyScreeningMetadataDto
{
    public string? GenerationSource { get; set; }
    public string? ObservedRegime { get; set; }
    public string? ReserveTargetRegime { get; set; }
    public bool LiveHaircutApplied { get; set; }
    public bool IsAutoPromoted { get; set; }

    internal static StrategyScreeningMetadataDto? FromJson(string? screeningMetricsJson)
    {
        var metrics = ScreeningMetrics.FromJson(screeningMetricsJson);
        if (metrics is null)
            return null;

        return new StrategyScreeningMetadataDto
        {
            GenerationSource = metrics.GenerationSource,
            ObservedRegime = metrics.ObservedRegime,
            ReserveTargetRegime = metrics.ReserveTargetRegime,
            LiveHaircutApplied = metrics.LiveHaircutApplied,
            IsAutoPromoted = metrics.IsAutoPromoted,
        };
    }
}
