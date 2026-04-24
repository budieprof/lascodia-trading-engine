using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.ChartAnnotations.Commands.CreateChartAnnotation;

// ── Command + validator ───────────────────────────────────────────────────────

/// <summary>
/// Records a new <see cref="ChartAnnotation"/>. The caller's trading account
/// (read via <see cref="IEAOwnershipGuard"/>) is captured as the author — no
/// explicit <c>CreatedBy</c> field on the request so tests and the UI can't
/// lie about authorship.
/// </summary>
public class CreateChartAnnotationCommand : IRequest<ResponseData<long>>
{
    /// <summary>Chart this note belongs to. See <c>ChartAnnotation.Target</c> for conventions.</summary>
    public string   Target      { get; set; } = string.Empty;

    /// <summary>Optional symbol scope. Null creates a global (all-symbol) annotation.</summary>
    public string?  Symbol      { get; set; }

    /// <summary>UTC moment the annotation points at.</summary>
    public DateTime AnnotatedAt { get; set; }

    /// <summary>Human-readable note. 500-char cap mirrors the entity.</summary>
    public string   Body        { get; set; } = string.Empty;
}

public class CreateChartAnnotationCommandValidator : AbstractValidator<CreateChartAnnotationCommand>
{
    public CreateChartAnnotationCommandValidator()
    {
        RuleFor(x => x.Target).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Symbol).MaximumLength(12);
        RuleFor(x => x.Body).NotEmpty().MaximumLength(500);
        RuleFor(x => x.AnnotatedAt).NotEqual(default(DateTime));
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CreateChartAnnotationCommandHandler
    : IRequestHandler<CreateChartAnnotationCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public CreateChartAnnotationCommandHandler(
        IWriteApplicationDbContext context,
        IEAOwnershipGuard ownershipGuard)
    {
        _context = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<long>> Handle(
        CreateChartAnnotationCommand request, CancellationToken cancellationToken)
    {
        var createdBy = _ownershipGuard.GetCallerAccountId();
        if (createdBy is null)
            return ResponseData<long>.Init(0, false, "Authenticated trading account id required.", "-11");

        var entity = new ChartAnnotation
        {
            Target      = request.Target,
            Symbol      = string.IsNullOrWhiteSpace(request.Symbol) ? null : request.Symbol,
            AnnotatedAt = request.AnnotatedAt,
            Body        = request.Body,
            CreatedBy   = createdBy.Value,
            CreatedAt   = DateTime.UtcNow,
        };

        _context.GetDbContext().Set<ChartAnnotation>().Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Annotation created", "00");
    }
}
