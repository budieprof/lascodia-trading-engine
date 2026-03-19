using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.EconomicEvents.Queries.DTOs;

public class EconomicEventDto : IMapFrom<EconomicEvent>
{
    public long                Id          { get; set; }
    public string?             Title       { get; set; }
    public string?             Currency    { get; set; }
    public EconomicImpact      Impact      { get; set; }
    public DateTime            ScheduledAt { get; set; }
    public string?             Forecast    { get; set; }
    public string?             Previous    { get; set; }
    public string?             Actual      { get; set; }
    public EconomicEventSource Source      { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<EconomicEvent, EconomicEventDto>();
    }
}
