using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

/// <summary>
/// Data transfer object for an ML model used in signal scoring and prediction.
/// </summary>
public class MLModelDto : IMapFrom<MLModel>
{
    /// <summary>Database ID of the model.</summary>
    public long           Id                 { get; set; }

    /// <summary>Instrument symbol this model is trained for (e.g. "EURUSD").</summary>
    public string?        Symbol             { get; set; }

    /// <summary>Chart timeframe this model targets.</summary>
    public Timeframe      Timeframe          { get; set; }

    /// <summary>Semantic version identifier for the model (e.g. "v3.2.1").</summary>
    public string?        ModelVersion       { get; set; }

    /// <summary>File path to the serialised model weights on disk.</summary>
    public string?        FilePath           { get; set; }

    /// <summary>Current lifecycle status (Training, Active, Superseded, etc.).</summary>
    public MLModelStatus  Status             { get; set; }

    /// <summary>Whether this model is currently the active champion for its symbol/timeframe.</summary>
    public bool      IsActive           { get; set; }

    /// <summary>Direction prediction accuracy on the validation set (0.0 to 1.0).</summary>
    public decimal?  DirectionAccuracy  { get; set; }

    /// <summary>Root Mean Square Error of magnitude predictions in pips.</summary>
    public decimal?  MagnitudeRMSE      { get; set; }

    /// <summary>Number of training samples used to build this model.</summary>
    public int       TrainingSamples    { get; set; }

    /// <summary>UTC time when the model was trained.</summary>
    public DateTime  TrainedAt          { get; set; }

    /// <summary>UTC time when the model was activated for live scoring, if applicable.</summary>
    public DateTime? ActivatedAt        { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MLModel, MLModelDto>();
    }
}
