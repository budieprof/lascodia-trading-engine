using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradeSignals.Queries.DTOs;

/// <summary>Read-only projection of the <see cref="TradeSignal"/> entity for API responses and query results.</summary>
public class TradeSignalDto : IMapFrom<TradeSignal>
{
    /// <summary>Unique trade signal identifier.</summary>
    public long            Id                   { get; set; }
    /// <summary>Strategy that generated this signal.</summary>
    public long            StrategyId           { get; set; }
    /// <summary>Currency pair symbol.</summary>
    public string?         Symbol               { get; set; }
    /// <summary>Trade direction (Buy/Sell).</summary>
    public TradeDirection  Direction            { get; set; }
    /// <summary>Suggested entry price.</summary>
    public decimal         EntryPrice           { get; set; }
    /// <summary>Suggested stop-loss price level.</summary>
    public decimal?        StopLoss             { get; set; }
    /// <summary>Suggested take-profit price level.</summary>
    public decimal?        TakeProfit           { get; set; }
    /// <summary>Recommended position size in lots.</summary>
    public decimal         SuggestedLotSize     { get; set; }
    /// <summary>Strategy confidence score (0.0 to 1.0).</summary>
    public decimal         Confidence           { get; set; }
    /// <summary>ML model's predicted direction, if scored.</summary>
    public TradeDirection? MLPredictedDirection { get; set; }
    /// <summary>ML model's predicted magnitude in pips.</summary>
    public decimal?        MLPredictedMagnitude { get; set; }
    /// <summary>ML model's confidence score.</summary>
    public decimal?        MLConfidenceScore    { get; set; }
    /// <summary>Identifier of the ML model that scored this signal.</summary>
    public long?           MLModelId            { get; set; }
    /// <summary>Current signal lifecycle status.</summary>
    public TradeSignalStatus Status             { get; set; }
    /// <summary>Reason for rejection, if the signal was rejected.</summary>
    public string?         RejectionReason      { get; set; }
    /// <summary>Assigned order identifier, if an order was created from this signal.</summary>
    public long?           OrderId              { get; set; }
    /// <summary>Timestamp when the signal was generated.</summary>
    public DateTime        GeneratedAt          { get; set; }
    /// <summary>Timestamp after which the signal is no longer valid.</summary>
    public DateTime        ExpiresAt            { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<TradeSignal, TradeSignalDto>();
    }
}
