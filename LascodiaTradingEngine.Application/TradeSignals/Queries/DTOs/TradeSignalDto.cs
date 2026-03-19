using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradeSignals.Queries.DTOs;

public class TradeSignalDto : IMapFrom<TradeSignal>
{
    public long            Id                   { get; set; }
    public long            StrategyId           { get; set; }
    public string?         Symbol               { get; set; }
    public TradeDirection  Direction            { get; set; }
    public decimal         EntryPrice           { get; set; }
    public decimal?        StopLoss             { get; set; }
    public decimal?        TakeProfit           { get; set; }
    public decimal         SuggestedLotSize     { get; set; }
    public decimal         Confidence           { get; set; }
    public TradeDirection? MLPredictedDirection { get; set; }
    public decimal?        MLPredictedMagnitude { get; set; }
    public decimal?        MLConfidenceScore    { get; set; }
    public long?           MLModelId            { get; set; }
    public TradeSignalStatus Status             { get; set; }
    public string?         RejectionReason      { get; set; }
    public long?           OrderId              { get; set; }
    public DateTime        GeneratedAt          { get; set; }
    public DateTime        ExpiresAt            { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<TradeSignal, TradeSignalDto>();
    }
}
