using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Positions.Queries.DTOs;

public class PositionDto : IMapFrom<Position>
{
    public long              Id                { get; set; }
    public string?           Symbol            { get; set; }
    public PositionDirection Direction         { get; set; }
    public decimal           OpenLots          { get; set; }
    public decimal           AverageEntryPrice { get; set; }
    public decimal?          CurrentPrice      { get; set; }
    public decimal           UnrealizedPnL     { get; set; }
    public decimal           RealizedPnL       { get; set; }
    public decimal           Swap              { get; set; }
    public decimal           Commission        { get; set; }
    public decimal?          StopLoss          { get; set; }
    public decimal?          TakeProfit        { get; set; }
    public PositionStatus    Status            { get; set; }
    public bool              IsPaper           { get; set; }
    public decimal?          TrailingStopLevel { get; set; }
    public string?           BrokerPositionId  { get; set; }
    public DateTime          OpenedAt          { get; set; }
    public DateTime?         ClosedAt          { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Position, PositionDto>();
    }
}
