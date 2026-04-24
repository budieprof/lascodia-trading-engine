using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.ChartAnnotations.Commands.DeleteChartAnnotation;

/// <summary>Soft-deletes a <see cref="ChartAnnotation"/>. Author-only — the caller must match the row's <c>CreatedBy</c>.</summary>
public class DeleteChartAnnotationCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

public class DeleteChartAnnotationCommandValidator : AbstractValidator<DeleteChartAnnotationCommand>
{
    public DeleteChartAnnotationCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
    }
}

public class DeleteChartAnnotationCommandHandler
    : IRequestHandler<DeleteChartAnnotationCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public DeleteChartAnnotationCommandHandler(
        IWriteApplicationDbContext context,
        IEAOwnershipGuard ownershipGuard)
    {
        _context = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(
        DeleteChartAnnotationCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<ChartAnnotation>()
            .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);

        if (entity is null)
            return ResponseData<string>.Init(null, false, "Annotation not found", "-14");

        var callerAccountId = _ownershipGuard.GetCallerAccountId();
        if (callerAccountId is not null && entity.CreatedBy != callerAccountId)
            return ResponseData<string>.Init(null, false, "Only the author can delete this annotation.", "-11");

        entity.IsDeleted = true;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Annotation deleted", "00");
    }
}
