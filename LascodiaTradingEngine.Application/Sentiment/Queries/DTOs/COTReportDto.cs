using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Sentiment.Queries.DTOs;

public class COTReportDto : IMapFrom<COTReport>
{
    public long     Id                          { get; set; }
    public string   Currency                    { get; set; } = string.Empty;
    public DateTime ReportDate                  { get; set; }
    public long     CommercialLong              { get; set; }
    public long     CommercialShort             { get; set; }
    public long     NonCommercialLong           { get; set; }
    public long     NonCommercialShort          { get; set; }
    public long     RetailLong                  { get; set; }
    public long     RetailShort                 { get; set; }
    public decimal  NetNonCommercialPositioning { get; set; }
    public decimal  NetPositioningChangeWeekly  { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<COTReport, COTReportDto>();
    }
}
