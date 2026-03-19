using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.AuditTrail.Queries.DTOs;

public class DecisionLogDto : IMapFrom<DecisionLog>
{
    public long     Id           { get; set; }
    public string?  EntityType   { get; set; }
    public long     EntityId     { get; set; }
    public string?  DecisionType { get; set; }
    public string?  Outcome      { get; set; }
    public string?  Reason       { get; set; }
    public string?  ContextJson  { get; set; }
    public string?  Source       { get; set; }
    public DateTime CreatedAt    { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<DecisionLog, DecisionLogDto>();
    }
}
