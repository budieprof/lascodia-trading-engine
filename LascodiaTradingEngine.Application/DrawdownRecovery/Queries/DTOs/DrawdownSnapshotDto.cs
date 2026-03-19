using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.DrawdownRecovery.Queries.DTOs;

public class DrawdownSnapshotDto : IMapFrom<DrawdownSnapshot>
{
    public long         Id            { get; set; }
    public decimal      CurrentEquity { get; set; }
    public decimal      PeakEquity    { get; set; }
    public decimal      DrawdownPct   { get; set; }
    public RecoveryMode RecoveryMode  { get; set; }
    public DateTime     RecordedAt    { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<DrawdownSnapshot, DrawdownSnapshotDto>();
    }
}
