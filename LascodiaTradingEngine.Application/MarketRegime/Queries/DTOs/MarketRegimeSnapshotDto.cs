using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.MarketRegime.Queries.DTOs;

/// <summary>
/// Read-only projection of a <see cref="MarketRegimeSnapshot"/> for API consumers.
/// Contains the detected regime classification and the indicator values that informed it.
/// </summary>
public class MarketRegimeSnapshotDto : IMapFrom<MarketRegimeSnapshot>
{
    /// <summary>Primary key of the snapshot record.</summary>
    public long         Id                { get; set; }

    /// <summary>Instrument symbol this regime was detected for (e.g. "EURUSD").</summary>
    public string?      Symbol            { get; set; }

    /// <summary>Chart timeframe the detection was performed on.</summary>
    public Timeframe    Timeframe         { get; set; }

    /// <summary>Detected market regime classification (Trending, Ranging, HighVolatility, etc.).</summary>
    public MarketRegimeEnum Regime        { get; set; }

    /// <summary>Confidence score of the regime classification (0–1).</summary>
    public decimal  Confidence        { get; set; }

    /// <summary>Average Directional Index value at detection time — measures trend strength.</summary>
    public decimal  ADX               { get; set; }

    /// <summary>Average True Range value at detection time — measures volatility.</summary>
    public decimal  ATR               { get; set; }

    /// <summary>Bollinger Band width at detection time — measures price compression/expansion.</summary>
    public decimal  BollingerBandWidth { get; set; }

    /// <summary>UTC timestamp when this regime was detected.</summary>
    public DateTime DetectedAt        { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MarketRegimeSnapshot, MarketRegimeSnapshotDto>();
    }
}
