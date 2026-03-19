using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Brokers.Queries.DTOs;

public class BrokerDto : IMapFrom<Broker>
{
    public long              Id            { get; set; }
    public string?           Name          { get; set; }
    public BrokerType        BrokerType    { get; set; }
    public BrokerEnvironment Environment   { get; set; }
    public string?           BaseUrl       { get; set; }
    public bool              IsActive      { get; set; }
    public bool              IsPaper       { get; set; }
    public BrokerStatus      Status        { get; set; }
    public string?           StatusMessage { get; set; }
    public DateTime          CreatedAt     { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Broker, BrokerDto>();
    }
}
