using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting.Queries.DTOs;

/// <summary>
/// Data transfer object for a backtest run, including configuration, status, and serialized results.
/// </summary>
public class BacktestRunDto : IMapFrom<BacktestRun>
{
    public long      Id             { get; set; }
    public long      StrategyId     { get; set; }
    public string?   Symbol         { get; set; }
    public Timeframe Timeframe      { get; set; }
    public DateTime  FromDate       { get; set; }
    public DateTime  ToDate         { get; set; }
    public decimal   InitialBalance { get; set; }
    public RunStatus Status         { get; set; }
    public string?   ResultJson     { get; set; }
    public string?   ErrorMessage   { get; set; }
    public ValidationFailureCode? FailureCode { get; set; }
    public string?   FailureDetailsJson { get; set; }
    public ValidationQueueSource QueueSource { get; set; }
    public DateTime  StartedAt      { get; set; }
    public DateTime  QueuedAt       { get; set; }
    public DateTime  AvailableAt    { get; set; }
    public DateTime? ClaimedAt      { get; set; }
    public string?   ClaimedByWorkerId { get; set; }
    public DateTime? ExecutionStartedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? CompletedAt    { get; set; }
    public int       RetryCount     { get; set; }
    public int?      TotalTrades    { get; set; }
    public decimal?  WinRate        { get; set; }
    public decimal?  ProfitFactor   { get; set; }
    public decimal?  MaxDrawdownPct { get; set; }
    public decimal?  SharpeRatio    { get; set; }
    public decimal?  FinalBalance   { get; set; }
    public decimal?  TotalReturn    { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<BacktestRun, BacktestRunDto>();
    }
}
