using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Four-eyes approval workflow: requires two distinct authenticated accounts to approve
/// before a high-impact operation executes. Implements segregation of duties.
/// Controlled by EngineConfig key <c>FourEyes:Enabled</c> (default: false).
/// When disabled, all operations are auto-approved with zero friction.
/// </summary>
[RegisterService]
public class ApprovalWorkflowService : IApprovalWorkflow
{
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly ILogger<ApprovalWorkflowService> _logger;

    /// <summary>Default approval expiry in hours.</summary>
    private const int DefaultExpiryHours = 24;

    /// <summary>EngineConfig key that controls whether four-eyes approval is enforced.</summary>
    private const string EnabledConfigKey = "FourEyes:Enabled";

    public ApprovalWorkflowService(
        IWriteApplicationDbContext writeContext,
        ILogger<ApprovalWorkflowService> logger)
    {
        _writeContext  = writeContext;
        _logger        = logger;
    }

    /// <summary>
    /// Returns true when four-eyes approval is enabled via EngineConfig.
    /// Defaults to false (auto-approve all operations).
    /// </summary>
    private async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        var config = await _writeContext.GetDbContext()
            .Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == EnabledConfigKey && !c.IsDeleted, cancellationToken);

        return config is not null
            && bool.TryParse(config.Value, out var enabled)
            && enabled;
    }

    public async Task<ApprovalRequest> RequestApprovalAsync(
        ApprovalOperationType operationType,
        long targetEntityId,
        string targetEntityType,
        string description,
        string changePayloadJson,
        long requestedByAccountId,
        CancellationToken cancellationToken)
    {
        // When disabled, return a synthetic approved request — the caller will
        // see IsApprovedAsync() return true and skip this path entirely, but
        // guard against direct calls as well.
        if (!await IsEnabledAsync(cancellationToken))
        {
            return new ApprovalRequest
            {
                OperationType     = operationType,
                Status            = ApprovalStatus.Consumed,
                TargetEntityId    = targetEntityId,
                TargetEntityType  = targetEntityType,
                Description       = "Auto-approved (FourEyes:Enabled = false)",
                ChangePayloadJson = changePayloadJson,
                RequestedByAccountId = requestedByAccountId,
                RequestedAt       = DateTime.UtcNow,
                ResolvedAt        = DateTime.UtcNow,
                ExpiresAt         = DateTime.UtcNow
            };
        }

        // Check for existing pending request on same target
        var existing = await _writeContext.GetDbContext()
            .Set<ApprovalRequest>()
            .FirstOrDefaultAsync(r =>
                r.OperationType == operationType &&
                r.TargetEntityId == targetEntityId &&
                r.Status == ApprovalStatus.Pending &&
                !r.IsDeleted, cancellationToken);

        if (existing is not null)
        {
            _logger.LogWarning(
                "Approval request already pending for {Type} on {EntityType}:{EntityId}",
                operationType, targetEntityType, targetEntityId);
            return existing;
        }

        var request = new ApprovalRequest
        {
            OperationType        = operationType,
            Status               = ApprovalStatus.Pending,
            TargetEntityId       = targetEntityId,
            TargetEntityType     = targetEntityType,
            Description          = description,
            ChangePayloadJson    = changePayloadJson,
            RequestedByAccountId = requestedByAccountId,
            RequestedAt          = DateTime.UtcNow,
            ExpiresAt            = DateTime.UtcNow.AddHours(DefaultExpiryHours)
        };

        await _writeContext.GetDbContext().Set<ApprovalRequest>().AddAsync(request, cancellationToken);
        await _writeContext.GetDbContext().SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Approval requested: {Type} on {EntityType}:{EntityId} by account {Account}",
            operationType, targetEntityType, targetEntityId, requestedByAccountId);

        return request;
    }

    public async Task<ApprovalRequest> ApproveAsync(
        long approvalRequestId,
        long approverAccountId,
        string? comment,
        CancellationToken cancellationToken)
    {
        var request = await _writeContext.GetDbContext()
            .Set<ApprovalRequest>()
            .FirstOrDefaultAsync(r => r.Id == approvalRequestId && !r.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Approval request {approvalRequestId} not found");

        if (request.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException($"Request {approvalRequestId} is not pending (status={request.Status})");

        if (request.ExpiresAt < DateTime.UtcNow)
        {
            request.Status = ApprovalStatus.Expired;
            await _writeContext.GetDbContext().SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException($"Request {approvalRequestId} has expired");
        }

        // Four-eyes: approver must differ from requester
        if (approverAccountId == request.RequestedByAccountId)
            throw new InvalidOperationException(
                "Four-eyes violation: approver must be different from requester");

        request.Status              = ApprovalStatus.Approved;
        request.ApprovedByAccountId = approverAccountId;
        request.ApproverComment     = comment;
        request.ResolvedAt          = DateTime.UtcNow;

        await _writeContext.GetDbContext().SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Approval granted: request {Id} ({Type}) approved by account {Account}",
            approvalRequestId, request.OperationType, approverAccountId);

        return request;
    }

    public async Task<ApprovalRequest> RejectAsync(
        long approvalRequestId,
        long approverAccountId,
        string reason,
        CancellationToken cancellationToken)
    {
        var request = await _writeContext.GetDbContext()
            .Set<ApprovalRequest>()
            .FirstOrDefaultAsync(r => r.Id == approvalRequestId && !r.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Approval request {approvalRequestId} not found");

        if (request.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException($"Request {approvalRequestId} is not pending");

        request.Status              = ApprovalStatus.Rejected;
        request.ApprovedByAccountId = approverAccountId;
        request.ApproverComment     = reason;
        request.ResolvedAt          = DateTime.UtcNow;

        await _writeContext.GetDbContext().SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Approval rejected: request {Id} ({Type}) by account {Account}: {Reason}",
            approvalRequestId, request.OperationType, approverAccountId, reason);

        return request;
    }

    public async Task<bool> IsApprovedAsync(
        ApprovalOperationType operationType,
        long targetEntityId,
        CancellationToken cancellationToken)
    {
        if (!await IsEnabledAsync(cancellationToken))
            return true;

        return await _writeContext.GetDbContext()
            .Set<ApprovalRequest>()
            .AnyAsync(r =>
                r.OperationType == operationType &&
                r.TargetEntityId == targetEntityId &&
                r.Status == ApprovalStatus.Approved &&
                !r.IsDeleted, cancellationToken);
    }

    public async Task<bool> ConsumeApprovalAsync(
        ApprovalOperationType operationType,
        long targetEntityId,
        CancellationToken cancellationToken)
    {
        if (!await IsEnabledAsync(cancellationToken))
            return true;

        // Atomic update: the WHERE clause ensures only Approved rows are consumed,
        // preventing TOCTOU races where two concurrent requests both see Approved
        // before either consumes.
        var consumed = await _writeContext.GetDbContext()
            .Set<ApprovalRequest>()
            .Where(r =>
                r.OperationType == operationType &&
                r.TargetEntityId == targetEntityId &&
                r.Status == ApprovalStatus.Approved &&
                !r.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ApprovalStatus.Consumed)
                .SetProperty(r => r.ResolvedAt, DateTime.UtcNow),
                cancellationToken);

        if (consumed == 0)
            _logger.LogWarning(
                "ConsumeApproval: no Approved rows found for {Type} on entity {EntityId} — possible race or stale approval",
                operationType, targetEntityId);

        return consumed > 0;
    }
}
