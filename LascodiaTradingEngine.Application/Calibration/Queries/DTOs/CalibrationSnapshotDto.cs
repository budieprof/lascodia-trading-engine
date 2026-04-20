using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;

namespace LascodiaTradingEngine.Application.Calibration.Queries.DTOs;

/// <summary>
/// Read projection for one <c>CalibrationSnapshot</c> row. Surfaces the
/// monthly aggregate so operators can chart gate hit rates over time and
/// justify threshold recalibration with data.
/// </summary>
public class CalibrationSnapshotDto : IMapFrom<LascodiaTradingEngine.Domain.Entities.CalibrationSnapshot>
{
    public long     Id                 { get; set; }
    public DateTime PeriodStart        { get; set; }
    public DateTime PeriodEnd          { get; set; }
    public string   PeriodGranularity  { get; set; } = "Monthly";
    public string   Stage              { get; set; } = string.Empty;
    public string   Reason             { get; set; } = string.Empty;
    public long     RejectionCount     { get; set; }
    public int      DistinctSymbols    { get; set; }
    public int      DistinctStrategies { get; set; }
    public DateTime ComputedAt         { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<LascodiaTradingEngine.Domain.Entities.CalibrationSnapshot, CalibrationSnapshotDto>();
    }
}
