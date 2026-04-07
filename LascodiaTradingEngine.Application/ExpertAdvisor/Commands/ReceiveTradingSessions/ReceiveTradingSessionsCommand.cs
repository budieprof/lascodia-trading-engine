using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveTradingSessions;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Receives trading session schedule data from the EA (e.g. market open/close times
/// for each symbol) and stores it for the session filter to consume.
/// </summary>
public class ReceiveTradingSessionsCommand : IRequest<ResponseData<string>>
{
    /// <summary>Unique identifier of the EA instance providing session data.</summary>
    public required string InstanceId { get; set; }

    /// <summary>List of trading session schedules for one or more symbols.</summary>
    public List<TradingSessionItem> Sessions { get; set; } = new();
}

/// <summary>
/// Represents a single trading session schedule for a symbol (e.g. London session for EURUSD).
/// </summary>
public class TradingSessionItem
{
    /// <summary>Instrument symbol this session applies to.</summary>
    public required string Symbol     { get; set; }

    /// <summary>Session name (e.g. "London", "NewYork", "Tokyo", "Sydney").</summary>
    public required string SessionName { get; set; }

    /// <summary>Session open time (time of day, UTC).</summary>
    public TimeSpan OpenTime          { get; set; }

    /// <summary>Session close time (time of day, UTC).</summary>
    public TimeSpan CloseTime         { get; set; }

    /// <summary>Day of week the session starts (0 = Sunday, 1 = Monday, etc.).</summary>
    public int      DayOfWeekStart    { get; set; }

    /// <summary>Day of week the session ends.</summary>
    public int      DayOfWeekEnd      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates InstanceId is non-empty and at least one session item is provided.
/// </summary>
public class ReceiveTradingSessionsCommandValidator : AbstractValidator<ReceiveTradingSessionsCommand>
{
    public ReceiveTradingSessionsCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Sessions)
            .NotEmpty().WithMessage("Sessions list cannot be empty");

        RuleForEach(x => x.Sessions).ChildRules(session =>
        {
            session.RuleFor(s => s.Symbol).NotEmpty().WithMessage("Session symbol cannot be empty");
            session.RuleFor(s => s.SessionName).NotEmpty().WithMessage("SessionName cannot be empty");
            session.RuleFor(s => s.DayOfWeekStart).InclusiveBetween(0, 6).WithMessage("DayOfWeekStart must be between 0 (Sunday) and 6 (Saturday)");
            session.RuleFor(s => s.DayOfWeekEnd).InclusiveBetween(0, 6).WithMessage("DayOfWeekEnd must be between 0 (Sunday) and 6 (Saturday)");
        });
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles trading session schedule ingestion. Persists session data as JSON in EngineConfig
/// (for backward compatibility) and also stores individual records in the TradingSessionSchedule table.
/// The SessionFilter reads from both sources to determine whether a symbol is within an active trading session.
/// </summary>
public class ReceiveTradingSessionsCommandHandler : IRequestHandler<ReceiveTradingSessionsCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ReceiveTradingSessionsCommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(ReceiveTradingSessionsCommand request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var dbContext = _context.GetDbContext();
        var configKey = $"EA:TradingSessions:{request.InstanceId}";
        var jsonValue = JsonSerializer.Serialize(request.Sessions);

        // Upsert the session data into EngineConfig using the instance-scoped key.
        // IgnoreQueryFilters so we can reuse a soft-deleted row if one exists.
        var existing = await dbContext.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Key == configKey, cancellationToken);

        if (existing is not null)
        {
            existing.Value = jsonValue;
            existing.LastUpdatedAt = DateTime.UtcNow;
            existing.IsDeleted = false;
        }
        else
        {
            dbContext.Set<EngineConfig>().Add(new EngineConfig
            {
                Key = configKey,
                Value = jsonValue,
                Description = $"Trading session schedules reported by EA instance {request.InstanceId}",
                DataType = ConfigDataType.Json,
                IsHotReloadable = true,
                LastUpdatedAt = DateTime.UtcNow,
            });
        }

        // Batch-load existing schedules to avoid N+1 queries
        var existingSchedules = await dbContext.Set<TradingSessionSchedule>()
            .Where(s => s.InstanceId == request.InstanceId && !s.IsDeleted)
            .ToListAsync(cancellationToken);
        var scheduleLookup = existingSchedules
            .ToDictionary(s => (s.Symbol, s.SessionName));

        foreach (var session in request.Sessions)
        {
            if (scheduleLookup.TryGetValue((session.Symbol, session.SessionName), out var existingSchedule))
            {
                existingSchedule.OpenTime = session.OpenTime;
                existingSchedule.CloseTime = session.CloseTime;
                existingSchedule.DayOfWeekStart = session.DayOfWeekStart;
                existingSchedule.DayOfWeekEnd = session.DayOfWeekEnd;
            }
            else
            {
                dbContext.Set<TradingSessionSchedule>().Add(new TradingSessionSchedule
                {
                    Symbol = session.Symbol,
                    SessionName = session.SessionName,
                    OpenTime = session.OpenTime,
                    CloseTime = session.CloseTime,
                    DayOfWeekStart = session.DayOfWeekStart,
                    DayOfWeekEnd = session.DayOfWeekEnd,
                    InstanceId = request.InstanceId,
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(configKey, true, "Successful", "00");
    }
}
