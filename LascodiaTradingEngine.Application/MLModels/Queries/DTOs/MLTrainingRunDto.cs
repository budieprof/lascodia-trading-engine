using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

/// <summary>
/// Data transfer object for an ML training run, representing one execution of the training pipeline.
/// </summary>
public class MLTrainingRunDto : IMapFrom<MLTrainingRun>
{
    /// <summary>Database ID of the training run.</summary>
    public long        Id                { get; set; }

    /// <summary>Instrument symbol the run was trained on.</summary>
    public string?     Symbol            { get; set; }

    /// <summary>Chart timeframe for the training data.</summary>
    public Timeframe   Timeframe         { get; set; }

    /// <summary>What triggered this run (Manual, Scheduled, Drift, etc.).</summary>
    public TriggerType TriggerType       { get; set; }

    /// <summary>Current run status (Queued, Running, Completed, Failed).</summary>
    public RunStatus   Status            { get; set; }

    /// <summary>Start of the training data window.</summary>
    public DateTime  FromDate          { get; set; }

    /// <summary>End of the training data window.</summary>
    public DateTime  ToDate            { get; set; }

    /// <summary>Total number of samples in the training dataset.</summary>
    public int       TotalSamples      { get; set; }

    /// <summary>Direction prediction accuracy on the validation set.</summary>
    public decimal?  DirectionAccuracy { get; set; }

    /// <summary>Root Mean Square Error of magnitude predictions.</summary>
    public decimal?  MagnitudeRMSE     { get; set; }

    /// <summary>ID of the MLModel produced by this run, if completed successfully.</summary>
    public long?     MLModelId         { get; set; }

    /// <summary>Error message if the run failed.</summary>
    public string?   ErrorMessage      { get; set; }

    /// <summary>UTC time when the run started.</summary>
    public DateTime  StartedAt         { get; set; }

    /// <summary>UTC time when the run completed (success or failure).</summary>
    public DateTime? CompletedAt       { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MLTrainingRun, MLTrainingRunDto>();
    }
}
