using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Orders.Queries.DTOs;

public class OrderDto : IMapFrom<Order>
{
    public long Id { get; set; }
    public int BusinessId { get; set; }
    public string? Symbol { get; set; }
    public string? OrderType { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Order, OrderDto>();
    }
}
