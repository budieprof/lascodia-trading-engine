using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Governance.Commands.CreateApprovalRequest;

/// <summary>Creates a four-eyes approval request for a high-impact operation.</summary>
public class CreateApprovalRequestCommand : IRequest<ResponseData<long>>
{
    public required string OperationType { get; set; }
    public long TargetEntityId { get; set; }
    public required string TargetEntityType { get; set; }
    public required string Description { get; set; }
    public string ChangePayloadJson { get; set; } = "{}";
    public long RequestedByAccountId { get; set; }
}

public class CreateApprovalRequestCommandValidator : AbstractValidator<CreateApprovalRequestCommand>
{
    public CreateApprovalRequestCommandValidator()
    {
        RuleFor(x => x.OperationType).NotEmpty()
            .Must(t => Enum.TryParse<ApprovalOperationType>(t, true, out _))
            .WithMessage("Invalid operation type");
        RuleFor(x => x.TargetEntityId).GreaterThan(0);
        RuleFor(x => x.TargetEntityType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.RequestedByAccountId).GreaterThan(0);
    }
}

public class CreateApprovalRequestCommandHandler : IRequestHandler<CreateApprovalRequestCommand, ResponseData<long>>
{
    private readonly IApprovalWorkflow _workflow;

    public CreateApprovalRequestCommandHandler(IApprovalWorkflow workflow)
    {
        _workflow = workflow;
    }

    public async Task<ResponseData<long>> Handle(
        CreateApprovalRequestCommand request,
        CancellationToken cancellationToken)
    {
        var operationType = Enum.Parse<ApprovalOperationType>(request.OperationType, true);

        var approval = await _workflow.RequestApprovalAsync(
            operationType,
            request.TargetEntityId,
            request.TargetEntityType,
            request.Description,
            request.ChangePayloadJson,
            request.RequestedByAccountId,
            cancellationToken);

        return ResponseData<long>.Init(approval.Id, true, "Approval request created", "00");
    }
}
