using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLEvaluation.Queries.DTOs;

/// <summary>
/// Data transfer object for an ML shadow evaluation, comparing a challenger model against the current champion.
/// </summary>
public class MLShadowEvaluationDto : IMapFrom<MLShadowEvaluation>
{
    /// <summary>Database ID of the shadow evaluation.</summary>
    public long                     Id                            { get; set; }

    /// <summary>Database ID of the challenger model being evaluated.</summary>
    public long                     ChallengerModelId             { get; set; }

    /// <summary>Database ID of the current champion model.</summary>
    public long                     ChampionModelId               { get; set; }

    /// <summary>Instrument symbol for the evaluation.</summary>
    public string?                  Symbol                        { get; set; }

    /// <summary>Chart timeframe for the evaluation.</summary>
    public Timeframe                Timeframe                     { get; set; }

    /// <summary>Current evaluation status (Running, Completed, etc.).</summary>
    public ShadowEvaluationStatus   Status                        { get; set; }

    /// <summary>Minimum trades required before a promotion decision can be made.</summary>
    public int                      RequiredTrades                { get; set; }

    /// <summary>Number of trades completed so far in the evaluation.</summary>
    public int                      CompletedTrades               { get; set; }

    /// <summary>Champion model's direction prediction accuracy during the evaluation.</summary>
    public decimal                  ChampionDirectionAccuracy     { get; set; }

    /// <summary>Champion model's magnitude prediction correlation.</summary>
    public decimal                  ChampionMagnitudeCorrelation  { get; set; }

    /// <summary>Champion model's Brier score (calibration quality; lower is better).</summary>
    public decimal                  ChampionBrierScore            { get; set; }

    /// <summary>Challenger model's direction prediction accuracy during the evaluation.</summary>
    public decimal                  ChallengerDirectionAccuracy   { get; set; }

    /// <summary>Challenger model's magnitude prediction correlation.</summary>
    public decimal                  ChallengerMagnitudeCorrelation { get; set; }

    /// <summary>Challenger model's Brier score (calibration quality; lower is better).</summary>
    public decimal                  ChallengerBrierScore          { get; set; }

    /// <summary>Final promotion decision (Promote, Reject, Inconclusive), if evaluation is complete.</summary>
    public PromotionDecision?       PromotionDecision             { get; set; }

    /// <summary>Human-readable explanation of the promotion decision.</summary>
    public string?                  DecisionReason                { get; set; }

    /// <summary>UTC time when the evaluation started.</summary>
    public DateTime                 StartedAt                     { get; set; }

    /// <summary>UTC time when the evaluation completed, if applicable.</summary>
    public DateTime?                CompletedAt                   { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MLShadowEvaluation, MLShadowEvaluationDto>();
    }
}
