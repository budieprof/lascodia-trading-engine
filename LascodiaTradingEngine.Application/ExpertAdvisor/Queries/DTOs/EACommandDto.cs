using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Queries.DTOs;

/// <summary>
/// Data transfer object for an EA command queued for execution by a specific EA instance.
/// </summary>
public class EACommandDto : IMapFrom<EACommand>
{
    /// <summary>Database ID of the command.</summary>
    public long           Id               { get; set; }

    /// <summary>InstanceId of the EA instance this command is targeted at.</summary>
    public string         TargetInstanceId { get; set; } = string.Empty;

    /// <summary>Type of command (ModifySLTP, ClosePosition, CancelOrder, UpdateTrailing, RequestBackfill, UpdateConfig).</summary>
    public EACommandType  CommandType      { get; set; }

    /// <summary>Broker ticket number of the target position or order, if applicable.</summary>
    public long?          TargetTicket     { get; set; }

    /// <summary>Symbol the command applies to (or "*" for all symbols).</summary>
    public string         Symbol           { get; set; } = string.Empty;

    /// <summary>JSON-encoded parameters for the command (e.g. new SL/TP values, config overrides).</summary>
    public string?        Parameters       { get; set; }

    /// <summary>Whether the EA has acknowledged processing this command.</summary>
    public bool           Acknowledged     { get; set; }

    /// <summary>UTC time when the command was acknowledged, if applicable.</summary>
    public DateTime?      AcknowledgedAt   { get; set; }

    /// <summary>Result details from the EA's acknowledgement (e.g. "Success", error message).</summary>
    public string?        AckResult        { get; set; }

    /// <summary>UTC time when the command was created/queued.</summary>
    public DateTime       CreatedAt        { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<EACommand, EACommandDto>();
    }
}
