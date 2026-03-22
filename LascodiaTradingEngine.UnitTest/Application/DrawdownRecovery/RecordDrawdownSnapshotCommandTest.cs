using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.DrawdownRecovery.Commands.RecordDrawdownSnapshot;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.DrawdownRecovery;

public class RecordDrawdownSnapshotCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly RecordDrawdownSnapshotCommandHandler _handler;
    private readonly RecordDrawdownSnapshotCommandValidator _validator;

    public RecordDrawdownSnapshotCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext = new Mock<DbContext>();
        var snapshots = new List<DrawdownSnapshot>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<DrawdownSnapshot>()).Returns(snapshots.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler = new RecordDrawdownSnapshotCommandHandler(_mockWriteContext.Object);
        _validator = new RecordDrawdownSnapshotCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_CurrentEquity_Is_Negative()
    {
        var command = new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = -100m,
            PeakEquity = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.CurrentEquity)
              .WithErrorMessage("CurrentEquity must be >= 0");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_PeakEquity_Is_Zero()
    {
        var command = new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = 5000m,
            PeakEquity = 0m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.PeakEquity)
              .WithErrorMessage("PeakEquity must be > 0");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_PeakEquity_Is_Negative()
    {
        var command = new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = 5000m,
            PeakEquity = -1m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.PeakEquity)
              .WithErrorMessage("PeakEquity must be > 0");
    }

    [Fact]
    public async Task Validator_Should_Pass_When_CurrentEquity_Is_Zero()
    {
        var command = new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = 0m,
            PeakEquity = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = 8000m,
            PeakEquity = 10000m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Normal_When_Drawdown_Below_10_Percent()
    {
        var command = new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = 9500m,
            PeakEquity = 10000m
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Normal", result.data);
    }

    [Fact]
    public async Task Handler_Should_Return_Reduced_When_Drawdown_Between_10_And_20_Percent()
    {
        var command = new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = 8500m,
            PeakEquity = 10000m
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Reduced", result.data);
    }

    [Fact]
    public async Task Handler_Should_Return_Halted_When_Drawdown_Is_20_Percent_Or_More()
    {
        var command = new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = 8000m,
            PeakEquity = 10000m
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Halted", result.data);
    }
}
