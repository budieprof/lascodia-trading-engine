using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.WalkForward.Queries.DTOs;

/// <summary>Data transfer object for a walk-forward analysis run including window configuration, scores, and status.</summary>
public class WalkForwardRunDto : IMapFrom<WalkForwardRun>
{
    public long      Id                        { get; set; }
    public long      StrategyId                { get; set; }
    public string?   Symbol                    { get; set; }
    public Timeframe Timeframe                 { get; set; }
    public DateTime  FromDate                  { get; set; }
    public DateTime  ToDate                    { get; set; }
    public int       InSampleDays              { get; set; }
    public int       OutOfSampleDays           { get; set; }
    public RunStatus Status                    { get; set; }
    public decimal  InitialBalance            { get; set; }
    public decimal? AverageOutOfSampleScore   { get; set; }
    public decimal? ScoreConsistency          { get; set; }
    public string?  WindowResultsJson         { get; set; }
    public string?  ErrorMessage              { get; set; }
    public ValidationFailureCode? FailureCode { get; set; }
    public string? FailureDetailsJson         { get; set; }
    public ValidationQueueSource QueueSource  { get; set; }
    public DateTime StartedAt                 { get; set; }
    public DateTime QueuedAt                  { get; set; }
    public DateTime AvailableAt               { get; set; }
    public DateTime? ClaimedAt                { get; set; }
    public string? ClaimedByWorkerId          { get; set; }
    public DateTime? ExecutionStartedAt       { get; set; }
    public DateTime? LastAttemptAt            { get; set; }
    public DateTime? CompletedAt              { get; set; }
    public int RetryCount                     { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<WalkForwardRun, WalkForwardRunDto>();
    }
}
