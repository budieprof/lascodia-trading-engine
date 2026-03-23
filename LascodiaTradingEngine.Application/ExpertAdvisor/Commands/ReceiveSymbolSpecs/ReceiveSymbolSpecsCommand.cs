using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveSymbolSpecs;

// ── Command ───────────────────────────────────────────────────────────────────

public class ReceiveSymbolSpecsCommand : IRequest<ResponseData<string>>
{
    public required string InstanceId { get; set; }
    public List<SymbolSpecItem> Specs { get; set; } = new();
}

public class SymbolSpecItem
{
    public required string Symbol     { get; set; }
    public int    Digits              { get; set; }
    public decimal ContractSize       { get; set; }
    public decimal MinVolume          { get; set; }
    public decimal MaxVolume          { get; set; }
    public decimal VolumeStep         { get; set; }
    public string  BaseCurrency       { get; set; } = string.Empty;
    public string  QuoteCurrency      { get; set; } = string.Empty;
}

// ── Validator ─────────────────────────────────────────────────────────────────

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
