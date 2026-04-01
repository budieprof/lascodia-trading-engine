using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Four-eyes approval workflow for high-impact operations. Requires two distinct
/// authenticated accounts to approve before execution.
/// </summary>
public interface IApprovalWorkflow
{
    Task<ApprovalRequest> RequestApprovalAsync(
        ApprovalOperationType operationType,
        long targetEntityId,
        string targetEntityType,
        string description,
        string changePayloadJson,
        long requestedByAccountId,
        CancellationToken cancellationToken);

    Task<ApprovalRequest> ApproveAsync(
        long approvalRequestId,
        long approverAccountId,
        string? comment,
        CancellationToken cancellationToken);

    Task<ApprovalRequest> RejectAsync(
        long approvalRequestId,
        long approverAccountId,
        string reason,
        CancellationToken cancellationToken);

    Task<bool> IsApprovedAsync(
        ApprovalOperationType operationType,
        long targetEntityId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically consumes an approved approval so it cannot be re-used for subsequent operations.
    /// Returns true if an approval was consumed, false if no approved row was found (e.g. race condition).
    /// Should be called after the approved operation completes successfully.
    /// </summary>
    Task<bool> ConsumeApprovalAsync(
        ApprovalOperationType operationType,
        long targetEntityId,
        CancellationToken cancellationToken);
}
