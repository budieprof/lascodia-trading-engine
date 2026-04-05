using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Alerts.Queries.DTOs;

/// <summary>
/// Data transfer object representing an alert rule and its current state.
/// </summary>
public class AlertDto : IMapFrom<Alert>
{
    /// <summary>Unique identifier of the alert.</summary>
    public long         Id              { get; set; }
    /// <summary>The type of market event that triggers this alert.</summary>
    public AlertType    AlertType       { get; set; }
    /// <summary>The trading symbol being monitored.</summary>
    public string?      Symbol          { get; set; }
    /// <summary>The notification delivery channel.</summary>
    public AlertChannel Channel         { get; set; }
    /// <summary>The channel-specific destination address.</summary>
    public string?   Destination     { get; set; }
    /// <summary>JSON-encoded condition parameters for the alert trigger.</summary>
    public string?   ConditionJson   { get; set; }
    /// <summary>Whether the alert is currently active and monitoring.</summary>
    public bool      IsActive        { get; set; }
    /// <summary>UTC timestamp of the last time this alert was triggered, if ever.</summary>
    public DateTime? LastTriggeredAt { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Alert, AlertDto>();
    }
}
