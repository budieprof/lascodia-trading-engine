using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLEvaluation.Queries.DTOs;

public class MLModelPredictionLogDto : IMapFrom<MLModelPredictionLog>
{
    public long           Id                      { get; set; }
    public long           TradeSignalId           { get; set; }
    public long           MLModelId               { get; set; }
    public ModelRole      ModelRole               { get; set; }
    public string?        Symbol                  { get; set; }
    public Timeframe      Timeframe               { get; set; }
    public TradeDirection PredictedDirection      { get; set; }
    public decimal        PredictedMagnitudePips  { get; set; }
    public decimal        ConfidenceScore         { get; set; }
    public decimal?       RawProbability          { get; set; }
    public decimal?       CalibratedProbability   { get; set; }
    public decimal?       ServedCalibratedProbability { get; set; }
    public TradeDirection? ActualDirection        { get; set; }
    public decimal?       ActualMagnitudePips     { get; set; }
    public bool?          WasProfitable           { get; set; }
    public bool?          DirectionCorrect        { get; set; }
    public DateTime       PredictedAt             { get; set; }
    public DateTime?      OutcomeRecordedAt       { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MLModelPredictionLog, MLModelPredictionLogDto>();
    }
}
