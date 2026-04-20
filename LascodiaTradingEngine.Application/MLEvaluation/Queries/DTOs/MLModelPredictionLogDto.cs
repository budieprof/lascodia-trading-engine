using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLEvaluation.Queries.DTOs;

/// <summary>
/// Data transfer object for an ML model prediction log entry, recording a single prediction
/// made by an ML model for a trade signal along with its actual outcome (when available).
/// </summary>
public class MLModelPredictionLogDto : IMapFrom<MLModelPredictionLog>
{
    /// <summary>Database ID of the prediction log entry.</summary>
    public long           Id                      { get; set; }

    /// <summary>ID of the trade signal this prediction was made for.</summary>
    public long           TradeSignalId           { get; set; }

    /// <summary>ID of the ML model that made this prediction.</summary>
    public long           MLModelId               { get; set; }

    /// <summary>Role of the model when the prediction was made (Champion or Challenger).</summary>
    public ModelRole      ModelRole               { get; set; }

    /// <summary>Instrument symbol the prediction was made for.</summary>
    public string?        Symbol                  { get; set; }

    /// <summary>Chart timeframe of the prediction.</summary>
    public Timeframe      Timeframe               { get; set; }

    /// <summary>Predicted trade direction (Buy or Sell).</summary>
    public TradeDirection PredictedDirection      { get; set; }

    /// <summary>Predicted price movement magnitude in pips.</summary>
    public decimal        PredictedMagnitudePips  { get; set; }

    /// <summary>Model's confidence score for this prediction (0.0 to 1.0).</summary>
    public decimal        ConfidenceScore         { get; set; }

    /// <summary>Raw (uncalibrated) probability from the model output.</summary>
    public decimal?       RawProbability          { get; set; }

    /// <summary>Platt-scaled calibrated probability.</summary>
    public decimal?       CalibratedProbability   { get; set; }

    /// <summary>Calibrated probability at the time this prediction was served to the signal pipeline.</summary>
    public decimal?       ServedCalibratedProbability { get; set; }

    /// <summary>Decision threshold used to serve this prediction.</summary>
    public decimal?       DecisionThresholdUsed  { get; set; }

    /// <summary>Conformal calibration active when this prediction was served.</summary>
    public long?          MLConformalCalibrationId { get; set; }

    /// <summary>Prediction-time conformal threshold used to build the served set.</summary>
    public double?        ConformalThresholdUsed { get; set; }

    /// <summary>Prediction-time target coverage.</summary>
    public double?        ConformalTargetCoverageUsed { get; set; }

    /// <summary>JSON array of labels in the served conformal prediction set.</summary>
    public string?        ConformalPredictionSetJson { get; set; }

    /// <summary>Actual market direction observed after the signal (populated by RecordPredictionOutcome).</summary>
    public TradeDirection? ActualDirection        { get; set; }

    /// <summary>Actual price movement magnitude in pips (populated by RecordPredictionOutcome).</summary>
    public decimal?       ActualMagnitudePips     { get; set; }

    /// <summary>Whether the trade was profitable based on actual outcome.</summary>
    public bool?          WasProfitable           { get; set; }

    /// <summary>Whether the predicted direction matched the actual direction.</summary>
    public bool?          DirectionCorrect        { get; set; }

    /// <summary>Nonconformity score computed once the actual direction is known.</summary>
    public double?        ConformalNonConformityScore { get; set; }

    /// <summary>Whether the served conformal set contained the actual direction.</summary>
    public bool?          WasConformalCovered     { get; set; }

    /// <summary>UTC time when the prediction was made.</summary>
    public DateTime       PredictedAt             { get; set; }

    /// <summary>UTC time when the actual outcome was recorded, if available.</summary>
    public DateTime?      OutcomeRecordedAt       { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MLModelPredictionLog, MLModelPredictionLogDto>();
    }
}
