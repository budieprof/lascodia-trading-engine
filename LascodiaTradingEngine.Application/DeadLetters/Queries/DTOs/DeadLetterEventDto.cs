using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.DeadLetters.Queries.DTOs;

/// <summary>Data transfer object for a dead-lettered integration event including handler, payload, error, and resolution status.</summary>
public class DeadLetterEventDto : IMapFrom<DeadLetterEvent>
{
    public long     Id             { get; set; }
    public string?  HandlerName    { get; set; }
    public string?  EventType      { get; set; }
    public string?  EventPayload   { get; set; }
    public string?  ErrorMessage   { get; set; }
    public string?  StackTrace     { get; set; }
    public int      Attempts       { get; set; }
    public DateTime DeadLetteredAt { get; set; }
    public bool     IsResolved     { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<DeadLetterEvent, DeadLetterEventDto>();
    }
}
