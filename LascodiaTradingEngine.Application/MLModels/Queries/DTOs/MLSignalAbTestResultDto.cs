using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

/// <summary>
/// Terminal outcome of a signal-level champion/challenger A/B test.
/// </summary>
public class MLSignalAbTestResultDto : IMapFrom<MLSignalAbTestResult>
{
    public long Id { get; set; }
    public long ChampionModelId { get; set; }
    public long ChallengerModelId { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public Timeframe Timeframe { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }

    public string Decision { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    public int ChampionTradeCount { get; set; }
    public int ChallengerTradeCount { get; set; }

    public decimal ChampionAvgPnl { get; set; }
    public decimal ChallengerAvgPnl { get; set; }
    public decimal ChampionSharpe { get; set; }
    public decimal ChallengerSharpe { get; set; }
    public decimal SprtLogLikelihoodRatio { get; set; }
    public decimal CovariateImbalanceScore { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MLSignalAbTestResult, MLSignalAbTestResultDto>();
    }
}
