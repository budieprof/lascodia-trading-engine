using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.RiskProfiles.Queries.DTOs;

/// <summary>Data transfer object for a risk profile including position limits, drawdown thresholds, and recovery parameters.</summary>
public class RiskProfileDto : IMapFrom<RiskProfile>
{
    public long    Id                           { get; set; }
    public string? Name                         { get; set; }
    public decimal MaxLotSizePerTrade           { get; set; }
    public decimal MaxDailyDrawdownPct          { get; set; }
    public decimal MaxTotalDrawdownPct          { get; set; }
    public int     MaxOpenPositions             { get; set; }
    public int     MaxDailyTrades               { get; set; }
    public decimal MaxRiskPerTradePct           { get; set; }
    public decimal MaxSymbolExposurePct         { get; set; }
    public bool    IsDefault                    { get; set; }
    public decimal DrawdownRecoveryThresholdPct { get; set; }
    public decimal RecoveryLotSizeMultiplier    { get; set; }
    public decimal RecoveryExitThresholdPct     { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<RiskProfile, RiskProfileDto>();
    }
}
