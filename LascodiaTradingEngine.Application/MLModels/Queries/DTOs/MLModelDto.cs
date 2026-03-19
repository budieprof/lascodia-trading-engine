using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

public class MLModelDto : IMapFrom<MLModel>
{
    public long           Id                 { get; set; }
    public string?        Symbol             { get; set; }
    public Timeframe      Timeframe          { get; set; }
    public string?        ModelVersion       { get; set; }
    public string?        FilePath           { get; set; }
    public MLModelStatus  Status             { get; set; }
    public bool      IsActive           { get; set; }
    public decimal?  DirectionAccuracy  { get; set; }
    public decimal?  MagnitudeRMSE      { get; set; }
    public int       TrainingSamples    { get; set; }
    public DateTime  TrainedAt          { get; set; }
    public DateTime? ActivatedAt        { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MLModel, MLModelDto>();
    }
}
