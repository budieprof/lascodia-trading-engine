using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveSymbolSpecs;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Receives symbol specification data from an EA instance and upserts CurrencyPair records.
/// Called during EA startup to synchronise broker-specific contract details (lot sizes, decimal places, etc.)
/// into the engine's symbol catalogue.
/// </summary>
public class ReceiveSymbolSpecsCommand : IRequest<ResponseData<string>>
{
    /// <summary>Unique identifier of the EA instance providing the symbol specs.</summary>
    public required string InstanceId { get; set; }

    /// <summary>List of symbol specifications to upsert into the CurrencyPair table.</summary>
    public List<SymbolSpecItem> Specs { get; set; } = new();
}

/// <summary>
/// Represents the contract specification for a single trading symbol as reported by MetaTrader 5.
/// </summary>
public class SymbolSpecItem
{
    /// <summary>Instrument symbol name (e.g. "EURUSD").</summary>
    public required string Symbol     { get; set; }

    /// <summary>Number of decimal places in the price (e.g. 5 for EURUSD = 1.12345).</summary>
    public int    Digits              { get; set; }

    /// <summary>Contract size in base currency units (e.g. 100000 for a standard forex lot).</summary>
    public decimal ContractSize       { get; set; }

    /// <summary>Minimum tradeable volume in lots.</summary>
    public decimal MinVolume          { get; set; }

    /// <summary>Maximum tradeable volume in lots.</summary>
    public decimal MaxVolume          { get; set; }

    /// <summary>Minimum volume increment (lot step).</summary>
    public decimal VolumeStep         { get; set; }

    /// <summary>Base currency of the pair (e.g. "EUR" for EURUSD).</summary>
    public string  BaseCurrency       { get; set; } = string.Empty;

    /// <summary>Quote currency of the pair (e.g. "USD" for EURUSD).</summary>
    public string  QuoteCurrency      { get; set; } = string.Empty;

    /// <summary>
    /// Per-night swap interest for <b>long</b> positions as reported by the broker
    /// (<c>SymbolInfoDouble(SYMBOL_SWAP_LONG)</c>). Units depend on <see cref="SwapMode"/>.
    /// Positive = broker pays you; negative = you pay broker. Defaults to 0 when the EA
    /// build pre-dates the swap-sync rollout; downstream carry logic treats 0 as "no data".
    /// </summary>
    public double SwapLong            { get; set; }

    /// <summary>Counterpart of <see cref="SwapLong"/> for <b>short</b> positions.</summary>
    public double SwapShort           { get; set; }

    /// <summary>
    /// Broker swap calculation mode (MT5 <c>SYMBOL_SWAP_MODE</c>: 0=Disabled, 1=Points,
    /// 2=Currency-Symbol, 3=Interest-Current, 4=Interest-Open, 5=Reopen-Current,
    /// 6=Reopen-Bid). Persisted as int so the engine stays MT5-agnostic.
    /// </summary>
    public int SwapMode               { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates that InstanceId is non-empty and at least one symbol spec is provided.
/// </summary>
public class ReceiveSymbolSpecsCommandValidator : AbstractValidator<ReceiveSymbolSpecsCommand>
{
    public ReceiveSymbolSpecsCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Specs)
            .NotEmpty().WithMessage("Specs list cannot be empty");

        RuleForEach(x => x.Specs).ChildRules(spec =>
        {
            spec.RuleFor(s => s.Symbol).NotEmpty().WithMessage("Symbol cannot be empty");
            spec.RuleFor(s => s.Digits).InclusiveBetween(0, 8).WithMessage("Digits must be between 0 and 8");
            spec.RuleFor(s => s.ContractSize).GreaterThan(0).WithMessage("ContractSize must be greater than zero");
            spec.RuleFor(s => s.MinVolume).GreaterThan(0).WithMessage("MinVolume must be greater than zero");
            spec.RuleFor(s => s.MaxVolume).GreaterThan(0).WithMessage("MaxVolume must be greater than zero");
            spec.RuleFor(s => s.MaxVolume).GreaterThanOrEqualTo(s => s.MinVolume).WithMessage("MaxVolume must be >= MinVolume");
            spec.RuleFor(s => s.VolumeStep).GreaterThan(0).WithMessage("VolumeStep must be greater than zero");
        });
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles symbol spec ingestion. For each spec, upserts the CurrencyPair record — updating
/// decimal places, contract size, lot constraints, and currencies for existing symbols, or
/// creating a new active CurrencyPair for unknown symbols.
/// </summary>
public class ReceiveSymbolSpecsCommandHandler : IRequestHandler<ReceiveSymbolSpecsCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ReceiveSymbolSpecsCommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(ReceiveSymbolSpecsCommand request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var dbContext = _context.GetDbContext();

        // Batch-load existing currency pairs to avoid N+1 queries.
        // Canonicalize via SymbolNormalizer so broker suffixes don't produce
        // duplicate CurrencyPair rows (one for "EURUSD", another for "EURUSD.a").
        var symbols = request.Specs
            .Select(s => LascodiaTradingEngine.Application.Common.Utilities.SymbolNormalizer.Normalize(s.Symbol))
            .Distinct()
            .ToList();
        var existingPairs = await dbContext
            .Set<Domain.Entities.CurrencyPair>()
            .Where(x => symbols.Contains(x.Symbol) && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        var pairBySymbol = existingPairs.ToDictionary(p => p.Symbol);

        foreach (var spec in request.Specs)
        {
            var symbol = LascodiaTradingEngine.Application.Common.Utilities.SymbolNormalizer.Normalize(spec.Symbol);

            if (pairBySymbol.TryGetValue(symbol, out var existing))
            {
                existing.DecimalPlaces = spec.Digits;
                existing.ContractSize  = spec.ContractSize;
                existing.MinLotSize    = spec.MinVolume;
                existing.MaxLotSize    = spec.MaxVolume;
                existing.LotStep       = spec.VolumeStep;
                existing.BaseCurrency  = spec.BaseCurrency;
                existing.QuoteCurrency = spec.QuoteCurrency;
                existing.SwapLong      = spec.SwapLong;
                existing.SwapShort     = spec.SwapShort;
                existing.SwapMode      = spec.SwapMode;
            }
            else
            {
                var newPair = new Domain.Entities.CurrencyPair
                {
                    Symbol        = symbol,
                    DecimalPlaces = spec.Digits,
                    ContractSize  = spec.ContractSize,
                    MinLotSize    = spec.MinVolume,
                    MaxLotSize    = spec.MaxVolume,
                    LotStep       = spec.VolumeStep,
                    BaseCurrency  = spec.BaseCurrency,
                    QuoteCurrency = spec.QuoteCurrency,
                    SwapLong      = spec.SwapLong,
                    SwapShort     = spec.SwapShort,
                    SwapMode      = spec.SwapMode,
                    IsActive      = true,
                };
                await dbContext.Set<Domain.Entities.CurrencyPair>().AddAsync(newPair, cancellationToken);
                pairBySymbol[symbol] = newPair;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
