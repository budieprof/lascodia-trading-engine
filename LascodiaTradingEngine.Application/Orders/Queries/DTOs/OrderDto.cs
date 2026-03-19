using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Queries.DTOs;

public class OrderDto : IMapFrom<Order>
{
    public long     Id             { get; set; }
    public long?    TradeSignalId  { get; set; }
    public string?  Symbol         { get; set; }
    public OrderType   OrderType      { get; set; }
    public ExecutionType ExecutionType { get; set; }
    public decimal  Quantity       { get; set; }
    public decimal  Price          { get; set; }
    public decimal? StopLoss       { get; set; }
    public decimal? TakeProfit     { get; set; }
    public decimal? FilledPrice    { get; set; }
    public decimal? FilledQuantity { get; set; }
    public OrderStatus Status       { get; set; }
    public string?  BrokerOrderId  { get; set; }
    public string?  RejectionReason { get; set; }
    public string?  Notes          { get; set; }
    public bool     IsPaper        { get; set; }
    public DateTime  CreatedAt     { get; set; }
    public DateTime? FilledAt      { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Order, OrderDto>();
    }
}
