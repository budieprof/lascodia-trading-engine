using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Queries.DTOs;

public class EACommandDto : IMapFrom<EACommand>
{
    public long           Id               { get; set; }
    public string         TargetInstanceId { get; set; } = string.Empty;
    public EACommandType  CommandType      { get; set; }
    public long?          TargetTicket     { get; set; }
    public string         Symbol           { get; set; } = string.Empty;
    public string?        Parameters       { get; set; }
    public bool           Acknowledged     { get; set; }
    public DateTime?      AcknowledgedAt   { get; set; }
    public string?        AckResult        { get; set; }
    public DateTime       CreatedAt        { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<EACommand, EACommandDto>();
    }
}
