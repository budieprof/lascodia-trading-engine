using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceivePositionSnapshot;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Receives a full snapshot of open positions from the broker via an EA instance.
/// Upserts engine Position records by matching on BrokerPositionId + Symbol.
/// Called during EA startup and periodically to reconcile broker state with the engine.
/// </summary>
public class ReceivePositionSnapshotCommand : IRequest<ResponseData<string>>
{
    /// <summary>Unique identifier of the EA instance providing the snapshot.</summary>
    public required string InstanceId { get; set; }

    /// <summary>List of open positions as reported by the broker. Capped at 500 items.</summary>
    public List<PositionSnapshotItem> Positions { get; set; } = new();
}

/// <summary>
/// Represents a single open position from the broker's perspective (MetaTrader 5 position data).
/// </summary>
public class PositionSnapshotItem
{
    /// <summary>Broker-assigned position ticket number.</summary>
    public long     Ticket          { get; set; }

    /// <summary>Instrument symbol (e.g. "EURUSD").</summary>
    public required string Symbol   { get; set; }

    /// <summary>Position direction: "Long" or "Short".</summary>
    public required string Direction { get; set; }

    /// <summary>Current open volume in lots.</summary>
    public decimal  Volume          { get; set; }

    /// <summary>Average entry price of the position.</summary>
    public decimal  OpenPrice       { get; set; }

    /// <summary>Current market price for the position's symbol.</summary>
    public decimal  CurrentPrice    { get; set; }

    /// <summary>Stop loss level, if set.</summary>
    public decimal? StopLoss        { get; set; }

    /// <summary>Take profit level, if set.</summary>
    public decimal? TakeProfit      { get; set; }

    /// <summary>Current unrealised profit/loss in account currency.</summary>
    public decimal  Profit          { get; set; }

    /// <summary>Accumulated swap charges.</summary>
    public decimal  Swap            { get; set; }

    /// <summary>Broker commission charged for this position.</summary>
    public decimal  Commission      { get; set; }

    /// <summary>UTC time when the position was opened.</summary>
    public DateTime OpenTime        { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates InstanceId is non-empty, Positions is not null, and batch size does not exceed 500 items.
/// </summary>
public class ReceivePositionSnapshotCommandValidator : AbstractValidator<ReceivePositionSnapshotCommand>
{
    public ReceivePositionSnapshotCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Positions)
            .NotNull().WithMessage("Positions cannot be null")
            .Must(p => p.Count <= 500).WithMessage("Position snapshot cannot exceed 500 items");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles position snapshot processing. Verifies caller ownership, then for each position:
/// updates the existing engine Position if matched by BrokerPositionId + Symbol, or creates a new
/// Position record for unknown broker positions. Flushes all changes in a single save.
/// </summary>
public class ReceivePositionSnapshotCommandHandler : IRequestHandler<ReceivePositionSnapshotCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ReceivePositionSnapshotCommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(ReceivePositionSnapshotCommand request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var dbContext = _context.GetDbContext();

        // Load the EA instance's owned symbols to validate each snapshot entry
        var eaInstance = await dbContext.Set<Domain.Entities.EAInstance>()
            .FirstOrDefaultAsync(x => x.InstanceId == request.InstanceId && !x.IsDeleted, cancellationToken);

        if (eaInstance is null)
            return ResponseData<string>.Init(null, false, "EA instance not found", "-14");

        var ownedSymbols = eaInstance.Symbols
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant())
            .ToHashSet();

        // Batch-load existing positions by broker ticket to avoid N+1 queries
        var brokerTickets = request.Positions.Select(p => p.Ticket.ToString()).Distinct().ToList();
        var existingPositions = await dbContext
            .Set<Domain.Entities.Position>()
            .Where(x => brokerTickets.Contains(x.BrokerPositionId!) && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        var positionLookup = existingPositions.ToDictionary(p => (p.BrokerPositionId!, p.Symbol));

        foreach (var snap in request.Positions)
        {
            var brokerTicket = snap.Ticket.ToString();
            // Canonicalize via SymbolNormalizer so broker suffixes (EURUSD.a,
            // EURUSD-pro, EUR/USD) collapse to the same key used by candles and
            // specs. Previously used raw ToUpperInvariant() — left broker-suffixed
            // symbols in the DB that silently failed downstream spec lookups.
            var symbol = LascodiaTradingEngine.Application.Common.Utilities.SymbolNormalizer.Normalize(snap.Symbol);

            // Hard-reject snapshots for symbols not owned by this EA instance to
            // prevent cross-instance data pollution and ensure strict symbol ownership.
            if (ownedSymbols.Count > 0 && !ownedSymbols.Contains(symbol))
                return ResponseData<string>.Init(null, false,
                    $"Symbol '{symbol}' is not owned by EA instance '{request.InstanceId}'", "-403");

            if (positionLookup.TryGetValue((brokerTicket, symbol), out var existing))
            {
                existing.CurrentPrice  = snap.CurrentPrice;
                existing.UnrealizedPnL = snap.Profit;
                existing.Swap          = snap.Swap;
                existing.Commission    = snap.Commission;
                existing.StopLoss      = snap.StopLoss;
                existing.TakeProfit    = snap.TakeProfit;
                existing.OpenLots      = snap.Volume;
            }
            else
            {
                if (!Enum.TryParse<PositionDirection>(snap.Direction, ignoreCase: true, out var direction))
                    continue; // Skip positions with unrecognised direction

                var newPosition = new Domain.Entities.Position
                {
                    Symbol            = symbol,
                    Direction         = direction,
                    OpenLots          = snap.Volume,
                    AverageEntryPrice = snap.OpenPrice,
                    CurrentPrice      = snap.CurrentPrice,
                    UnrealizedPnL     = snap.Profit,
                    Swap              = snap.Swap,
                    Commission        = snap.Commission,
                    StopLoss          = snap.StopLoss,
                    TakeProfit        = snap.TakeProfit,
                    Status            = PositionStatus.Open,
                    BrokerPositionId  = brokerTicket,
                    OpenedAt          = snap.OpenTime,
                };
                await dbContext.Set<Domain.Entities.Position>().AddAsync(newPosition, cancellationToken);
                positionLookup[(brokerTicket, symbol)] = newPosition;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
