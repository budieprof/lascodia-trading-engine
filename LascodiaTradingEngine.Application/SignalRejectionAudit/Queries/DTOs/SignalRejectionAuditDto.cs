using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using Domain = LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.SignalRejectionAuditNs.Queries.DTOs;

/// <summary>
/// Read projection for one <c>SignalRejectionAudit</c> row. Shape tracks the
/// entity exactly — the audit stream is intentionally narrow and operators
/// typically want everything surfaced.
/// </summary>
public class SignalRejectionAuditDto : IMapFrom<LascodiaTradingEngine.Domain.Entities.SignalRejectionAudit>
{
    /// <summary>Audit row ID (monotonic).</summary>
    public long     Id             { get; set; }

    /// <summary>FK to the TradeSignal, null for pre-creation rejections.</summary>
    public long?    TradeSignalId  { get; set; }

    /// <summary>Owning strategy ID (0 for tick-level rejections).</summary>
    public long     StrategyId     { get; set; }

    /// <summary>Currency pair.</summary>
    public string   Symbol         { get; set; } = string.Empty;

    /// <summary>Pipeline stage that rejected the signal.</summary>
    public string   Stage          { get; set; } = string.Empty;

    /// <summary>Short machine-readable rejection reason.</summary>
    public string   Reason         { get; set; } = string.Empty;

    /// <summary>Optional human-readable detail.</summary>
    public string?  Detail         { get; set; }

    /// <summary>Worker / service that recorded the rejection.</summary>
    public string   Source         { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the rejection.</summary>
    public DateTime RejectedAt     { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<LascodiaTradingEngine.Domain.Entities.SignalRejectionAudit, SignalRejectionAuditDto>();
    }
}
