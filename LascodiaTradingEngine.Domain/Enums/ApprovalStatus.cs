namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>Status of a four-eyes approval request.</summary>
public enum ApprovalStatus
{
    Pending  = 0,
    Approved = 1,
    Rejected = 2,
    Expired  = 3,
    Consumed = 4
}
