using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

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

    public ReceiveSymbolSpecsCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ReceiveSymbolSpecsCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();

        foreach (var spec in request.Specs)
        {
            var symbol = spec.Symbol.ToUpperInvariant();

            var existing = await dbContext
                .Set<Domain.Entities.CurrencyPair>()
                .FirstOrDefaultAsync(x => x.Symbol == symbol && !x.IsDeleted, cancellationToken);

            if (existing is not null)
            {
                existing.DecimalPlaces = spec.Digits;
                existing.ContractSize  = spec.ContractSize;
                existing.MinLotSize    = spec.MinVolume;
                existing.MaxLotSize    = spec.MaxVolume;
                existing.LotStep       = spec.VolumeStep;
                existing.BaseCurrency  = spec.BaseCurrency;
                existing.QuoteCurrency = spec.QuoteCurrency;
            }
            else
            {
                await dbContext.Set<Domain.Entities.CurrencyPair>().AddAsync(new Domain.Entities.CurrencyPair
                {
                    Symbol        = symbol,
                    DecimalPlaces = spec.Digits,
                    ContractSize  = spec.ContractSize,
                    MinLotSize    = spec.MinVolume,
                    MaxLotSize    = spec.MaxVolume,
                    LotStep       = spec.VolumeStep,
                    BaseCurrency  = spec.BaseCurrency,
                    QuoteCurrency = spec.QuoteCurrency,
                    IsActive      = true,
                }, cancellationToken);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
