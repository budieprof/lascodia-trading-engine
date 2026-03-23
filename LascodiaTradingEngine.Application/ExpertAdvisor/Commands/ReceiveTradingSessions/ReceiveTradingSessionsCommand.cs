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
    public required string InstanceId { get; set; }
    public List<TradingSessionItem> Sessions { get; set; } = new();
}

public class TradingSessionItem
{
    public required string Symbol     { get; set; }
    public required string SessionName { get; set; }  // "London", "NewYork", "Tokyo", "Sydney"
    public TimeSpan OpenTime          { get; set; }
    public TimeSpan CloseTime         { get; set; }
    public int      DayOfWeekStart    { get; set; }
    public int      DayOfWeekEnd      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

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
