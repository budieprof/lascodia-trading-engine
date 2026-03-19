using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.MarketRegime.Queries.DTOs;

public class MarketRegimeSnapshotDto : IMapFrom<MarketRegimeSnapshot>
{
    public long         Id                { get; set; }
    public string?      Symbol            { get; set; }
    public Timeframe    Timeframe         { get; set; }
    public MarketRegimeEnum Regime        { get; set; }
    public decimal  Confidence        { get; set; }
    public decimal  ADX               { get; set; }
    public decimal  ATR               { get; set; }
    public decimal  BollingerBandWidth { get; set; }
    public DateTime DetectedAt        { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MarketRegimeSnapshot, MarketRegimeSnapshotDto>();
    }
}
