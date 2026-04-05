using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.AuditTrail.Queries.DTOs;

/// <summary>
/// Data transfer object for an immutable decision log entry in the audit trail.
/// </summary>
public class DecisionLogDto : IMapFrom<DecisionLog>
{
    /// <summary>Unique identifier of the decision log entry.</summary>
    public long     Id           { get; set; }
    /// <summary>The type of entity the decision pertains to.</summary>
    public string?  EntityType   { get; set; }
    /// <summary>The ID of the entity involved in the decision.</summary>
    public long     EntityId     { get; set; }
    /// <summary>The category of decision made.</summary>
    public string?  DecisionType { get; set; }
    /// <summary>The result of the decision.</summary>
    public string?  Outcome      { get; set; }
    /// <summary>Human-readable explanation of the decision.</summary>
    public string?  Reason       { get; set; }
    /// <summary>Optional JSON context with before/after state.</summary>
    public string?  ContextJson  { get; set; }
    /// <summary>The component that originated this decision.</summary>
    public string?  Source       { get; set; }
    /// <summary>UTC timestamp when the decision was recorded.</summary>
    public DateTime CreatedAt    { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<DecisionLog, DecisionLogDto>();
    }
}
