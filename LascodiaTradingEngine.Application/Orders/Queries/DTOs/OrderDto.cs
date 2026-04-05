using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Queries.DTOs;

/// <summary>Read-only projection of the <see cref="Order"/> entity for API responses and query results.</summary>
public class OrderDto : IMapFrom<Order>
{
    /// <summary>Unique order identifier.</summary>
    public long     Id             { get; set; }
    /// <summary>Originating trade signal identifier, if any.</summary>
    public long?    TradeSignalId  { get; set; }
    /// <summary>Currency pair symbol.</summary>
    public string?  Symbol         { get; set; }
    /// <summary>Trade direction (Buy/Sell).</summary>
    public OrderType   OrderType      { get; set; }
    /// <summary>Execution method (Market/Limit/Stop/StopLimit).</summary>
    public ExecutionType ExecutionType { get; set; }
    /// <summary>Requested lot size.</summary>
    public decimal  Quantity       { get; set; }
    /// <summary>Requested price (zero for Market).</summary>
    public decimal  Price          { get; set; }
    /// <summary>Stop-loss price level.</summary>
    public decimal? StopLoss       { get; set; }
    /// <summary>Take-profit price level.</summary>
    public decimal? TakeProfit     { get; set; }
    /// <summary>Actual fill price from the broker.</summary>
    public decimal? FilledPrice    { get; set; }
    /// <summary>Actual filled quantity from the broker.</summary>
    public decimal? FilledQuantity { get; set; }
    /// <summary>Current order lifecycle status.</summary>
    public OrderStatus Status       { get; set; }
    /// <summary>Broker-assigned order ticket.</summary>
    public string?  BrokerOrderId  { get; set; }
    /// <summary>Reason for rejection, if the order was rejected.</summary>
    public string?  RejectionReason { get; set; }
    /// <summary>Free-text notes.</summary>
    public string?  Notes          { get; set; }
    /// <summary>Whether this is a paper-trading (simulated) order.</summary>
    public bool     IsPaper        { get; set; }
    /// <summary>Timestamp when the order was created.</summary>
    public DateTime  CreatedAt     { get; set; }
    /// <summary>Timestamp when the order was filled at the broker.</summary>
    public DateTime? FilledAt      { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Order, OrderDto>();
    }
}
