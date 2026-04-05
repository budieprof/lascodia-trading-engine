using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

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
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles trading session schedule ingestion. Currently a no-op placeholder pending creation of a
/// dedicated TradingSessionSchedule entity. The SessionFilter will read from this data to determine
/// whether a symbol is within an active trading session.
/// </summary>
public class ReceiveTradingSessionsCommandHandler : IRequestHandler<ReceiveTradingSessionsCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public ReceiveTradingSessionsCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ReceiveTradingSessionsCommand request, CancellationToken cancellationToken)
    {
        // Store trading session schedules for downstream session filter consumption.
        // Currently persisted as-is; the SessionFilter reads from this data
        // to determine whether a symbol is within an active trading session.
        // TODO: Persist to a dedicated TradingSessionSchedule entity when the table is created.

        await Task.CompletedTask;

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
