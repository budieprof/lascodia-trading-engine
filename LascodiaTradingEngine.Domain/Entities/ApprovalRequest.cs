using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Four-eyes approval request for high-impact operations. Requires two distinct
/// authenticated accounts to approve before the operation is executed.
/// Implements segregation of duties for operational risk governance.
/// </summary>
public class ApprovalRequest : Entity<long>
{
    /// <summary>Type of operation requiring approval.</summary>
    public ApprovalOperationType OperationType { get; set; }

    /// <summary>Current approval status.</summary>
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    /// <summary>ID of the target entity (strategy, model, risk profile, etc.).</summary>
    public long TargetEntityId { get; set; }

    /// <summary>Type name of the target entity for polymorphic lookup.</summary>
    public string TargetEntityType { get; set; } = string.Empty;

    /// <summary>Human-readable description of the requested change.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>JSON snapshot of the proposed change for audit.</summary>
    public string ChangePayloadJson { get; set; } = "{}";

    /// <summary>Account ID of the user who initiated the request.</summary>
    public long RequestedByAccountId { get; set; }

    /// <summary>Account ID of the second approver (must differ from requester).</summary>
    public long? ApprovedByAccountId { get; set; }

    /// <summary>Reason provided by the approver (required for rejections).</summary>
    public string? ApproverComment { get; set; }

    /// <summary>When the request was created.</summary>
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the request was approved or rejected.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Approval requests expire after this time if not acted upon.</summary>
    public DateTime ExpiresAt { get; set; }

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
