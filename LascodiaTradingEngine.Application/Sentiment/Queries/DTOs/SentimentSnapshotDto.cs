using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Sentiment.Queries.DTOs;

public class SentimentSnapshotDto : IMapFrom<SentimentSnapshot>
{
    public long            Id             { get; set; }
    public string          Currency       { get; set; } = string.Empty;
    public SentimentSource Source         { get; set; }
    public decimal  SentimentScore { get; set; }
    public decimal  Confidence     { get; set; }
    public string?  RawDataJson    { get; set; }
    public DateTime CapturedAt     { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<SentimentSnapshot, SentimentSnapshotDto>();
    }
}
