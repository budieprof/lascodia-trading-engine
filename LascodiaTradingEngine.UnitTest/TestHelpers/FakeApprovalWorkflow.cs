using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.TestHelpers;

/// <summary>Configurable fake IApprovalWorkflow for testing approval gates.</summary>
public class FakeApprovalWorkflow : IApprovalWorkflow
{
    /// <summary>Set to true to make IsApprovedAsync return true.</summary>
    public bool AlwaysApproved { get; set; } = false;

    /// <summary>Tracks requests made during tests.</summary>
    public List<(ApprovalOperationType Type, long EntityId)> RequestsMade { get; } = new();

    public Task<ApprovalRequest> RequestApprovalAsync(
        ApprovalOperationType operationType, long targetEntityId, string targetEntityType,
        string description, string changePayloadJson, long requestedByAccountId,
        CancellationToken cancellationToken)
    {
        RequestsMade.Add((operationType, targetEntityId));
        return Task.FromResult(EntityFactory.CreateApprovalRequest(operationType));
    }

    public Task<ApprovalRequest> ApproveAsync(long id, long approverAccountId, string? comment, CancellationToken ct)
        => Task.FromResult(EntityFactory.CreateApprovalRequest(status: ApprovalStatus.Approved));

    public Task<ApprovalRequest> RejectAsync(long id, long approverAccountId, string reason, CancellationToken ct)
        => Task.FromResult(EntityFactory.CreateApprovalRequest(status: ApprovalStatus.Rejected));

    public Task<bool> IsApprovedAsync(ApprovalOperationType operationType, long targetEntityId, CancellationToken ct)
        => Task.FromResult(AlwaysApproved);

    public Task<bool> ConsumeApprovalAsync(ApprovalOperationType operationType, long targetEntityId, CancellationToken ct)
        => Task.FromResult(true);
}
