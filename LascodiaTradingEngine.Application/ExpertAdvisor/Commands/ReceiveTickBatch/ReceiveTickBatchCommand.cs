using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveTickBatch;

// ── Command ───────────────────────────────────────────────────────────────────

public class ReceiveTickBatchCommand : IRequest<ResponseData<string>>
{
    public required string InstanceId { get; set; }
    public List<TickItem> Ticks { get; set; } = new();
}

public class TickItem
{
    public required string Symbol { get; set; }
    public decimal Bid       { get; set; }
    public decimal Ask       { get; set; }
    public DateTime Timestamp { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ReceiveTickBatchCommandValidator : AbstractValidator<ReceiveTickBatchCommand>
{
    public ReceiveTickBatchCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Ticks)
            .NotEmpty().WithMessage("Ticks list cannot be empty")
            .Must(t => t.Count <= 5000).WithMessage("Tick batch cannot exceed 5000 items");

        RuleForEach(x => x.Ticks).ChildRules(tick =>
        {
            tick.RuleFor(t => t.Symbol).NotEmpty().WithMessage("Tick symbol cannot be empty");
            tick.RuleFor(t => t.Bid).GreaterThan(0).WithMessage("Bid must be greater than zero");
            tick.RuleFor(t => t.Ask).GreaterThan(0).WithMessage("Ask must be greater than zero");
            tick.RuleFor(t => t.Ask).GreaterThanOrEqualTo(t => t.Bid).WithMessage("Ask must be >= Bid");
            tick.RuleFor(t => t.Timestamp).NotEmpty().WithMessage("Tick timestamp cannot be empty");
        });
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ReceiveTickBatchCommandHandler : IRequestHandler<ReceiveTickBatchCommand, ResponseData<string>>
{
    private readonly ILivePriceCache _cache;
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ReceiveTickBatchCommandHandler(
        ILivePriceCache cache,
        IWriteApplicationDbContext context,
        IIntegrationEventService eventBus,
        IEAOwnershipGuard ownershipGuard)
    {
        _cache          = cache;
        _context        = context;
        _eventBus       = eventBus;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(ReceiveTickBatchCommand request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        // Track unique symbols to publish one event per symbol (latest tick wins)
        var latestBySymbol = new Dictionary<string, TickItem>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        const int maxTickAgeSeconds = 60;
        int staleDropped = 0;

        foreach (var tick in request.Ticks)
        {
            // Reject ticks older than 60 seconds to prevent stale data from propagating
            if ((now - tick.Timestamp).TotalSeconds > maxTickAgeSeconds)
            {
                staleDropped++;
                continue;
            }

            // Reject ticks with future timestamps (clock skew > 5 seconds)
            if (tick.Timestamp > now.AddSeconds(5))
            {
                staleDropped++;
                continue;
            }

            var symbol = tick.Symbol.ToUpperInvariant();
            _cache.Update(symbol, tick.Bid, tick.Ask, tick.Timestamp);

            // Keep only the latest tick per symbol for event publishing
            if (!latestBySymbol.TryGetValue(symbol, out var existing) || tick.Timestamp > existing.Timestamp)
                latestBySymbol[symbol] = tick;
        }

        // Publish PriceUpdatedIntegrationEvent for each unique symbol
        foreach (var kvp in latestBySymbol)
        {
            var tick = kvp.Value;
            await _eventBus.SaveAndPublish(_context, new PriceUpdatedIntegrationEvent
            {
                Symbol    = kvp.Key,
                Bid       = tick.Bid,
                Ask       = tick.Ask,
                Timestamp = tick.Timestamp,
            });
        }

        string message = staleDropped > 0
            ? $"Successful ({staleDropped} stale/future tick(s) dropped)"
            : "Successful";
        return ResponseData<string>.Init(null, true, message, "00");
    }
}
