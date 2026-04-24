using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.ChartAnnotations.Commands.UpdateChartAnnotation;

/// <summary>
/// Edits the body of an existing <see cref="ChartAnnotation"/>. Target + symbol
/// + annotated-at are immutable — if the operator needs to move the note,
/// delete and re-create. Only the original author can edit (the ownership
/// check enforces this).
/// </summary>
public class UpdateChartAnnotationCommand : IRequest<ResponseData<string>>
{
    public long   Id   { get; set; }
    public string Body { get; set; } = string.Empty;
}

public class UpdateChartAnnotationCommandValidator : AbstractValidator<UpdateChartAnnotationCommand>
{
    public UpdateChartAnnotationCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.Body).NotEmpty().MaximumLength(500);
    }
}

public class UpdateChartAnnotationCommandHandler
    : IRequestHandler<UpdateChartAnnotationCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public UpdateChartAnnotationCommandHandler(
        IWriteApplicationDbContext context,
        IEAOwnershipGuard ownershipGuard)
    {
        _context = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(
        UpdateChartAnnotationCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<ChartAnnotation>()
            .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);

        if (entity is null)
            return ResponseData<string>.Init(null, false, "Annotation not found", "-14");

        var callerAccountId = _ownershipGuard.GetCallerAccountId();
        if (callerAccountId is not null && entity.CreatedBy != callerAccountId)
            return ResponseData<string>.Init(null, false, "Only the author can edit this annotation.", "-11");

        entity.Body      = request.Body;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Annotation updated", "00");
    }
}
