using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Alerts.Queries.DTOs;

public class AlertDto : IMapFrom<Alert>
{
    public long         Id              { get; set; }
    public AlertType    AlertType       { get; set; }
    public string?      Symbol          { get; set; }
    public AlertChannel Channel         { get; set; }
    public string?   Destination     { get; set; }
    public string?   ConditionJson   { get; set; }
    public bool      IsActive        { get; set; }
    public DateTime? LastTriggeredAt { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Alert, AlertDto>();
    }
}
