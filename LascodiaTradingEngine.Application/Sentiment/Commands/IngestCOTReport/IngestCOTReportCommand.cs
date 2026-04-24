using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Sentiment.Commands.IngestCOTReport;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Ingests a CFTC Commitments of Traders (COT) report containing commercial, non-commercial,
/// and retail positioning data for institutional sentiment analysis.
/// </summary>
public class IngestCOTReportCommand : IRequest<ResponseData<long>>
{
    public required string Symbol              { get; set; }
    public DateTime        ReportDate          { get; set; }
    public decimal         CommercialLong      { get; set; }
    public decimal         CommercialShort     { get; set; }
    public decimal         NonCommercialLong   { get; set; }
    public decimal         NonCommercialShort  { get; set; }
    public decimal         RetailLong          { get; set; }
    public decimal         RetailShort         { get; set; }
    public decimal         TotalOpenInterest   { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class IngestCOTReportCommandValidator : AbstractValidator<IngestCOTReportCommand>
{
    public IngestCOTReportCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol is required");

        RuleFor(x => x.ReportDate)
            .NotEmpty().WithMessage("ReportDate is required");

        RuleFor(x => x.TotalOpenInterest)
            .GreaterThanOrEqualTo(0).WithMessage("TotalOpenInterest must be >= 0");

        RuleFor(x => x.CommercialLong)
            .GreaterThanOrEqualTo(0).WithMessage("CommercialLong must be >= 0");

        RuleFor(x => x.CommercialShort)
            .GreaterThanOrEqualTo(0).WithMessage("CommercialShort must be >= 0");

        RuleFor(x => x.NonCommercialLong)
            .GreaterThanOrEqualTo(0).WithMessage("NonCommercialLong must be >= 0");

        RuleFor(x => x.NonCommercialShort)
            .GreaterThanOrEqualTo(0).WithMessage("NonCommercialShort must be >= 0");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Persists a COT report with upsert semantics (Currency + ReportDate is unique).
/// Computes net non-commercial positioning and the week-over-week change by querying
/// the previous published report for the same currency.
/// </summary>
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
        string currency = request.Symbol.Length >= 3
            ? request.Symbol[..3].ToUpperInvariant()
            : request.Symbol.ToUpperInvariant();
        DateTime reportDate = DateTime.SpecifyKind(request.ReportDate.Date, DateTimeKind.Utc);

        decimal netNonCommercial = request.NonCommercialLong - request.NonCommercialShort;

        // Query the previous week's report for this currency to compute week-over-week delta.
        var previousReport = await _context.GetDbContext()
            .Set<Domain.Entities.COTReport>()
            .Where(x => x.Currency == currency && x.ReportDate < reportDate && !x.IsDeleted)
            .OrderByDescending(x => x.ReportDate)
            .Select(x => new { x.NetNonCommercialPositioning })
            .FirstOrDefaultAsync(cancellationToken);

        decimal weeklyChange = previousReport != null
            ? netNonCommercial - previousReport.NetNonCommercialPositioning
            : 0m;

        // Upsert: update existing record for the same Currency + ReportDate, or insert new.
        var existing = await _context.GetDbContext()
            .Set<Domain.Entities.COTReport>()
            .FirstOrDefaultAsync(
                x => x.Currency == currency && x.ReportDate == reportDate && !x.IsDeleted,
                cancellationToken);

        if (existing != null)
        {
            existing.CommercialLong              = (long)request.CommercialLong;
            existing.CommercialShort             = (long)request.CommercialShort;
            existing.NonCommercialLong           = (long)request.NonCommercialLong;
            existing.NonCommercialShort          = (long)request.NonCommercialShort;
            existing.RetailLong                  = (long)request.RetailLong;
            existing.RetailShort                 = (long)request.RetailShort;
            existing.TotalOpenInterest           = (long)request.TotalOpenInterest;
            existing.NetNonCommercialPositioning = netNonCommercial;
            existing.NetPositioningChangeWeekly  = weeklyChange;

            await _context.SaveChangesAsync(cancellationToken);
            return ResponseData<long>.Init(existing.Id, true, "Successful", "00");
        }

        var entity = new Domain.Entities.COTReport
        {
            Currency                    = currency,
            ReportDate                  = reportDate,
            CommercialLong              = (long)request.CommercialLong,
            CommercialShort             = (long)request.CommercialShort,
            NonCommercialLong           = (long)request.NonCommercialLong,
            NonCommercialShort          = (long)request.NonCommercialShort,
            RetailLong                  = (long)request.RetailLong,
            RetailShort                 = (long)request.RetailShort,
            TotalOpenInterest           = (long)request.TotalOpenInterest,
            NetNonCommercialPositioning = netNonCommercial,
            NetPositioningChangeWeekly  = weeklyChange
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.COTReport>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
