using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;

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
            .NotEmpty().WithMessage("Ticks list cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ReceiveTickBatchCommandHandler : IRequestHandler<ReceiveTickBatchCommand, ResponseData<string>>
{
    private readonly ILivePriceCache _cache;
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;

    public ReceiveTickBatchCommandHandler(
        ILivePriceCache cache,
        IWriteApplicationDbContext context,
        IIntegrationEventService eventBus)
    {
        _cache    = cache;
        _context  = context;
        _eventBus = eventBus;
    }

    public async Task<ResponseData<string>> Handle(ReceiveTickBatchCommand request, CancellationToken cancellationToken)
    {
        // Track unique symbols to publish one event per symbol (latest tick wins)
        var latestBySymbol = new Dictionary<string, TickItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var tick in request.Ticks)
        {
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

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
