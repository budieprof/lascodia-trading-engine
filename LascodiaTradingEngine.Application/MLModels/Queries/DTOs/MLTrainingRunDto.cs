using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

public class MLTrainingRunDto : IMapFrom<MLTrainingRun>
{
    public long        Id                { get; set; }
    public string?     Symbol            { get; set; }
    public Timeframe   Timeframe         { get; set; }
    public TriggerType TriggerType       { get; set; }
    public RunStatus   Status            { get; set; }
    public DateTime  FromDate          { get; set; }
    public DateTime  ToDate            { get; set; }
    public int       TotalSamples      { get; set; }
    public decimal?  DirectionAccuracy { get; set; }
    public decimal?  MagnitudeRMSE     { get; set; }
    public long?     MLModelId         { get; set; }
    public string?   ErrorMessage      { get; set; }
    public DateTime  StartedAt         { get; set; }
    public DateTime? CompletedAt       { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MLTrainingRun, MLTrainingRunDto>();
    }
}
