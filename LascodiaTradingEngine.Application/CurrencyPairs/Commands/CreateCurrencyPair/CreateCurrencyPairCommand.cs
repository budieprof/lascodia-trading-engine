using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.CurrencyPairs.Commands.CreateCurrencyPair;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Registers a new tradable currency pair (or instrument) with its symbol specification.
/// Symbols are normalised to uppercase on creation.
/// </summary>
public class CreateCurrencyPairCommand : IRequest<ResponseData<long>>
{
    public required string Symbol        { get; set; }
    public required string BaseCurrency  { get; set; }
    public required string QuoteCurrency { get; set; }
    public int     DecimalPlaces { get; set; } = 5;
    public decimal ContractSize  { get; set; } = 100_000m;
    public decimal MinLotSize    { get; set; } = 0.01m;
    public decimal MaxLotSize    { get; set; } = 100m;
    public decimal LotStep       { get; set; } = 0.01m;
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates symbol, currency codes, lot constraints, and contract size for the new currency pair.</summary>
public class CreateCurrencyPairCommandValidator : AbstractValidator<CreateCurrencyPairCommand>
{
    public CreateCurrencyPairCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.BaseCurrency)
            .NotEmpty().WithMessage("BaseCurrency cannot be empty")
            .Length(3).WithMessage("BaseCurrency must be 3 characters");

        RuleFor(x => x.QuoteCurrency)
            .NotEmpty().WithMessage("QuoteCurrency cannot be empty")
            .Length(3).WithMessage("QuoteCurrency must be 3 characters");

        RuleFor(x => x.DecimalPlaces)
            .InclusiveBetween(0, 10).WithMessage("DecimalPlaces must be between 0 and 10");

        RuleFor(x => x.ContractSize)
            .GreaterThan(0).WithMessage("ContractSize must be greater than zero");

        RuleFor(x => x.MinLotSize)
            .GreaterThan(0).WithMessage("MinLotSize must be greater than zero");

        RuleFor(x => x.MaxLotSize)
            .GreaterThan(0).WithMessage("MaxLotSize must be greater than zero")
            .GreaterThan(x => x.MinLotSize).WithMessage("MaxLotSize must be greater than MinLotSize");

        RuleFor(x => x.LotStep)
            .GreaterThan(0).WithMessage("LotStep must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Persists a new currency pair entity with uppercase-normalised symbol and currency codes.</summary>
public class CreateCurrencyPairCommandHandler : IRequestHandler<CreateCurrencyPairCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public CreateCurrencyPairCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(CreateCurrencyPairCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.CurrencyPair
        {
            Symbol        = request.Symbol.ToUpperInvariant(),
            BaseCurrency  = request.BaseCurrency.ToUpperInvariant(),
            QuoteCurrency = request.QuoteCurrency.ToUpperInvariant(),
            DecimalPlaces = request.DecimalPlaces,
            ContractSize  = request.ContractSize,
            MinLotSize    = request.MinLotSize,
            MaxLotSize    = request.MaxLotSize,
            LotStep       = request.LotStep,
            IsActive      = true
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
