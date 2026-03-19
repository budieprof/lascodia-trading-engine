using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLEvaluation.Queries.DTOs;

public class MLShadowEvaluationDto : IMapFrom<MLShadowEvaluation>
{
    public long                     Id                            { get; set; }
    public long                     ChallengerModelId             { get; set; }
    public long                     ChampionModelId               { get; set; }
    public string?                  Symbol                        { get; set; }
    public Timeframe                Timeframe                     { get; set; }
    public ShadowEvaluationStatus   Status                        { get; set; }
    public int                      RequiredTrades                { get; set; }
    public int                      CompletedTrades               { get; set; }
    public decimal                  ChampionDirectionAccuracy     { get; set; }
    public decimal                  ChampionMagnitudeCorrelation  { get; set; }
    public decimal                  ChampionBrierScore            { get; set; }
    public decimal                  ChallengerDirectionAccuracy   { get; set; }
    public decimal                  ChallengerMagnitudeCorrelation { get; set; }
    public decimal                  ChallengerBrierScore          { get; set; }
    public PromotionDecision?       PromotionDecision             { get; set; }
    public string?                  DecisionReason                { get; set; }
    public DateTime                 StartedAt                     { get; set; }
    public DateTime?                CompletedAt                   { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MLShadowEvaluation, MLShadowEvaluationDto>();
    }
}
