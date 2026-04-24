using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

/// <summary>
/// Projection of an ML-drift-related <see cref="Alert"/> row exposed to the admin UI.
/// Surfaces the structured fields the drift workers write into <see cref="Alert.ConditionJson"/>
/// without requiring the client to parse the blob.
/// </summary>
public class DriftAlertDto : IMapFrom<Alert>
{
    public long          Id               { get; set; }
    public string?       Symbol           { get; set; }
    public AlertType     AlertType        { get; set; }
    public AlertSeverity Severity         { get; set; }

    /// <summary>Detector that raised the alert — parsed from <c>ConditionJson.DetectorType</c>.</summary>
    public string?       DetectorType     { get; set; }

    /// <summary>Raw condition payload; shape varies per detector.</summary>
    public string        ConditionJson    { get; set; } = "{}";

    public string?       DeduplicationKey { get; set; }
    public int           CooldownSeconds  { get; set; }
    public bool          IsActive         { get; set; }
    public DateTime?     LastTriggeredAt  { get; set; }
    public DateTime?     AutoResolvedAt   { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        // `DetectorType` is derived from ConditionJson in the handler, not via AutoMapper,
        // because the field isn't on the entity. Everything else is a straight copy.
        profile.CreateMap<Alert, DriftAlertDto>()
               .ForMember(d => d.DetectorType, m => m.Ignore());
    }
}
