using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Positions.Queries.DTOs;

/// <summary>Read-only projection of the <see cref="Position"/> entity for API responses and query results.</summary>
public class PositionDto : IMapFrom<Position>
{
    /// <summary>Unique position identifier.</summary>
    public long              Id                { get; set; }
    /// <summary>Currency pair symbol.</summary>
    public string?           Symbol            { get; set; }
    /// <summary>Position direction (Long/Short).</summary>
    public PositionDirection Direction         { get; set; }
    /// <summary>Current open lot size.</summary>
    public decimal           OpenLots          { get; set; }
    /// <summary>Weighted average entry price.</summary>
    public decimal           AverageEntryPrice { get; set; }
    /// <summary>Latest market price for the symbol.</summary>
    public decimal?          CurrentPrice      { get; set; }
    /// <summary>Mark-to-market unrealized profit/loss.</summary>
    public decimal           UnrealizedPnL     { get; set; }
    /// <summary>Cumulative realized profit/loss from partial and full closes.</summary>
    public decimal           RealizedPnL       { get; set; }
    /// <summary>Accumulated swap (rollover) charges.</summary>
    public decimal           Swap              { get; set; }
    /// <summary>Accumulated commission charges.</summary>
    public decimal           Commission        { get; set; }
    /// <summary>Stop-loss price level.</summary>
    public decimal?          StopLoss          { get; set; }
    /// <summary>Take-profit price level.</summary>
    public decimal?          TakeProfit        { get; set; }
    /// <summary>Current position lifecycle status.</summary>
    public PositionStatus    Status            { get; set; }
    /// <summary>Whether this is a paper-trading (simulated) position.</summary>
    public bool              IsPaper           { get; set; }
    /// <summary>Current trailing stop price level, if active.</summary>
    public decimal?          TrailingStopLevel { get; set; }
    /// <summary>Broker-assigned position ticket.</summary>
    public string?           BrokerPositionId  { get; set; }
    /// <summary>Timestamp when the position was opened.</summary>
    public DateTime          OpenedAt          { get; set; }
    /// <summary>Timestamp when the position was fully closed.</summary>
    public DateTime?         ClosedAt          { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Position, PositionDto>();
    }
}
