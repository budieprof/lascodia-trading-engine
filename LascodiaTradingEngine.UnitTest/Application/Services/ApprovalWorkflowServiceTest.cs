using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MockQueryable.Moq;
using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class ApprovalWorkflowServiceTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteCtx;
    private readonly Mock<DbContext> _mockDbCtx;
    private readonly ApprovalWorkflowService _service;

    public ApprovalWorkflowServiceTest()
    {
        _mockDbCtx = new Mock<DbContext>();
        _mockWriteCtx = new Mock<IWriteApplicationDbContext>();
        _mockWriteCtx.Setup(c => c.GetDbContext()).Returns(_mockDbCtx.Object);

        // Default empty approval set
        var emptySet = new List<ApprovalRequest>().AsQueryable().BuildMockDbSet();
        _mockDbCtx.Setup(c => c.Set<ApprovalRequest>()).Returns(emptySet.Object);

        // Default: FourEyes:Enabled = true so the full workflow is tested
        SetFourEyesEnabled(true);

        _service = new ApprovalWorkflowService(
            _mockWriteCtx.Object,
            Mock.Of<ILogger<ApprovalWorkflowService>>());
    }

    private void SetFourEyesEnabled(bool enabled)
    {
        var configs = new List<EngineConfig>
        {
            new() { Key = "FourEyes:Enabled", Value = enabled.ToString(), DataType = ConfigDataType.Bool }
        };
        _mockDbCtx.Setup(c => c.Set<EngineConfig>()).Returns(configs.AsQueryable().BuildMockDbSet().Object);
    }

    [Fact]
    public async Task RequestApprovalAsync_CreatesNewRequest()
    {
        var result = await _service.RequestApprovalAsync(
            ApprovalOperationType.ModelPromotion, 1, "MLModel", "Test", "{}", 10, CancellationToken.None);

        Assert.Equal(ApprovalStatus.Pending, result.Status);
        Assert.Equal(10, result.RequestedByAccountId);
    }

    [Fact]
    public async Task ApproveAsync_SameAccount_ThrowsFourEyesViolation()
    {
        var request = EntityFactory.CreateApprovalRequest();
        request.RequestedByAccountId = 5;
        var set = new List<ApprovalRequest> { request }.AsQueryable().BuildMockDbSet();
        _mockDbCtx.Setup(c => c.Set<ApprovalRequest>()).Returns(set.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ApproveAsync(request.Id, 5, null, CancellationToken.None)); // Same account
    }

    [Fact]
    public async Task ApproveAsync_DifferentAccount_Succeeds()
    {
        var request = EntityFactory.CreateApprovalRequest();
        request.RequestedByAccountId = 5;
        var set = new List<ApprovalRequest> { request }.AsQueryable().BuildMockDbSet();
        _mockDbCtx.Setup(c => c.Set<ApprovalRequest>()).Returns(set.Object);

        var result = await _service.ApproveAsync(request.Id, 10, "Approved", CancellationToken.None);

        Assert.Equal(ApprovalStatus.Approved, result.Status);
        Assert.Equal(10, result.ApprovedByAccountId);
    }

    [Fact]
    public async Task RejectAsync_SetsStatusAndReason()
    {
        var request = EntityFactory.CreateApprovalRequest();
        var set = new List<ApprovalRequest> { request }.AsQueryable().BuildMockDbSet();
        _mockDbCtx.Setup(c => c.Set<ApprovalRequest>()).Returns(set.Object);

        var result = await _service.RejectAsync(request.Id, 10, "Not approved", CancellationToken.None);

        Assert.Equal(ApprovalStatus.Rejected, result.Status);
        Assert.Equal("Not approved", result.ApproverComment);
    }

    [Fact]
    public async Task IsApprovedAsync_WhenDisabled_ReturnsTrue()
    {
        SetFourEyesEnabled(false);

        var result = await _service.IsApprovedAsync(
            ApprovalOperationType.ModelPromotion, 999, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task RequestApprovalAsync_WhenDisabled_ReturnsSyntheticConsumed()
    {
        SetFourEyesEnabled(false);

        var result = await _service.RequestApprovalAsync(
            ApprovalOperationType.StrategyActivation, 1, "Strategy", "Test", "{}", 10, CancellationToken.None);

        Assert.Equal(ApprovalStatus.Consumed, result.Status);
        Assert.Contains("Auto-approved", result.Description);
    }
}
