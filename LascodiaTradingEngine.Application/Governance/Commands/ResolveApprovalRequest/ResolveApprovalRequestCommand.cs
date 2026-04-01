using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Governance.Commands.ResolveApprovalRequest;

/// <summary>Approves or rejects a pending four-eyes approval request.</summary>
public class ResolveApprovalRequestCommand : IRequest<ResponseData<bool>>
{
    public long ApprovalRequestId { get; set; }
    public long ApproverAccountId { get; set; }
    public bool Approve { get; set; }
    public string? Comment { get; set; }
}

public class ResolveApprovalRequestCommandValidator : AbstractValidator<ResolveApprovalRequestCommand>
{
    public ResolveApprovalRequestCommandValidator()
    {
        RuleFor(x => x.ApprovalRequestId).GreaterThan(0);
        RuleFor(x => x.ApproverAccountId).GreaterThan(0);
        RuleFor(x => x.Comment)
            .NotEmpty().When(x => !x.Approve)
            .WithMessage("Reason is required for rejections");
    }
}

public class ResolveApprovalRequestCommandHandler : IRequestHandler<ResolveApprovalRequestCommand, ResponseData<bool>>
{
    private readonly IApprovalWorkflow _workflow;

    public ResolveApprovalRequestCommandHandler(IApprovalWorkflow workflow)
    {
        _workflow = workflow;
    }

    public async Task<ResponseData<bool>> Handle(
        ResolveApprovalRequestCommand request,
        CancellationToken cancellationToken)
    {
        if (request.Approve)
        {
            await _workflow.ApproveAsync(
                request.ApprovalRequestId,
                request.ApproverAccountId,
                request.Comment,
                cancellationToken);
        }
        else
        {
            await _workflow.RejectAsync(
                request.ApprovalRequestId,
                request.ApproverAccountId,
                request.Comment ?? "Rejected",
                cancellationToken);
        }

        return ResponseData<bool>.Init(true, true, "Successful", "00");
    }
}
