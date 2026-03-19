using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Sentiment.Commands.IngestCOTReport;

// ── Command ───────────────────────────────────────────────────────────────────

public class IngestCOTReportCommand : IRequest<ResponseData<long>>
{
    public required string Symbol              { get; set; }
    public DateTime        ReportDate          { get; set; }
    public decimal         CommercialLong      { get; set; }
    public decimal         CommercialShort     { get; set; }
    public decimal         NonCommercialLong   { get; set; }
    public decimal         NonCommercialShort  { get; set; }
    public decimal         TotalOpenInterest   { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class IngestCOTReportCommandValidator : AbstractValidator<IngestCOTReportCommand>
{
    public IngestCOTReportCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol is required");

        RuleFor(x => x.TotalOpenInterest)
            .GreaterThanOrEqualTo(0).WithMessage("TotalOpenInterest must be >= 0");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class IngestCOTReportCommandHandler : IRequestHandler<IngestCOTReportCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public IngestCOTReportCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(
        IngestCOTReportCommand request, CancellationToken cancellationToken)
    {
        // Extract the currency from the symbol (first 3 chars for base currency)
        string currency = request.Symbol.Length >= 3
            ? request.Symbol[..3].ToUpperInvariant()
            : request.Symbol.ToUpperInvariant();

        decimal netNonCommercial = request.NonCommercialLong - request.NonCommercialShort;

        var entity = new Domain.Entities.COTReport
        {
            Currency                    = currency,
            ReportDate                  = request.ReportDate,
            CommercialLong              = (long)request.CommercialLong,
            CommercialShort             = (long)request.CommercialShort,
            NonCommercialLong           = (long)request.NonCommercialLong,
            NonCommercialShort          = (long)request.NonCommercialShort,
            RetailLong                  = 0,
            RetailShort                 = 0,
            NetNonCommercialPositioning = netNonCommercial,
            NetPositioningChangeWeekly  = 0m
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.COTReport>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
