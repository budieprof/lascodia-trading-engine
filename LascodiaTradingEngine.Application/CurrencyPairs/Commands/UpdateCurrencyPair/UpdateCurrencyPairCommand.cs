using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.CurrencyPairs.Commands.UpdateCurrencyPair;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpdateCurrencyPairCommand : IRequest<ResponseData<string>>
{
    [JsonIgnore]
    public long Id { get; set; }

    public required string Symbol        { get; set; }
    public required string BaseCurrency  { get; set; }
    public required string QuoteCurrency { get; set; }
    public int     DecimalPlaces { get; set; }
    public decimal ContractSize  { get; set; }
    public decimal MinLotSize    { get; set; }
    public decimal MaxLotSize    { get; set; }
    public decimal LotStep       { get; set; }
    public bool    IsActive      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpdateCurrencyPairCommandValidator : AbstractValidator<UpdateCurrencyPairCommand>
{
    public UpdateCurrencyPairCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be provided");

        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.BaseCurrency)
            .NotEmpty().WithMessage("BaseCurrency cannot be empty")
            .Length(3).WithMessage("BaseCurrency must be 3 characters");

        RuleFor(x => x.QuoteCurrency)
            .NotEmpty().WithMessage("QuoteCurrency cannot be empty")
            .Length(3).WithMessage("QuoteCurrency must be 3 characters");

        RuleFor(x => x.ContractSize).GreaterThan(0);
        RuleFor(x => x.MinLotSize).GreaterThan(0);
        RuleFor(x => x.MaxLotSize)
            .GreaterThan(0)
            .GreaterThan(x => x.MinLotSize).WithMessage("MaxLotSize must be greater than MinLotSize");
        RuleFor(x => x.LotStep).GreaterThan(0);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpdateCurrencyPairCommandHandler : IRequestHandler<UpdateCurrencyPairCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateCurrencyPairCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(UpdateCurrencyPairCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<string>.Init(null, false, "Currency pair not found", "-14");

        entity.Symbol        = request.Symbol.ToUpperInvariant();
        entity.BaseCurrency  = request.BaseCurrency.ToUpperInvariant();
        entity.QuoteCurrency = request.QuoteCurrency.ToUpperInvariant();
        entity.DecimalPlaces = request.DecimalPlaces;
        entity.ContractSize  = request.ContractSize;
        entity.MinLotSize    = request.MinLotSize;
        entity.MaxLotSize    = request.MaxLotSize;
        entity.LotStep       = request.LotStep;
        entity.IsActive      = request.IsActive;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
