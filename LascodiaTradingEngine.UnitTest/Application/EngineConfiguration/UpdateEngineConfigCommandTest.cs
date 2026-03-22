using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.EngineConfiguration.Commands.UpsertEngineConfig;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.EngineConfiguration;

public class UpdateEngineConfigCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly UpsertEngineConfigCommandValidator _validator;

    public UpdateEngineConfigCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _validator = new UpsertEngineConfigCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Key_Is_Empty()
    {
        var command = new UpsertEngineConfigCommand
        {
            Key = string.Empty,
            Value = "100",
            DataType = "Int"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Key)
              .WithErrorMessage("Key is required");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Value_Is_Empty()
    {
        var command = new UpsertEngineConfigCommand
        {
            Key = "MaxOrderSize",
            Value = string.Empty,
            DataType = "Decimal"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Value)
              .WithErrorMessage("Value is required");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_DataType_Is_Invalid()
    {
        var command = new UpsertEngineConfigCommand
        {
            Key = "MaxOrderSize",
            Value = "100",
            DataType = "Float"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.DataType)
              .WithErrorMessage("DataType must be 'String', 'Int', 'Decimal', 'Bool', or 'Json'");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new UpsertEngineConfigCommand
        {
            Key = "MaxOrderSize",
            Value = "100",
            DataType = "Int"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Create_New_Config_When_Key_Not_Found()
    {
        // Arrange
        var configs = new List<EngineConfig>().AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<EngineConfig>()).Returns(configs.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var handler = new UpsertEngineConfigCommandHandler(_mockWriteContext.Object);
        var command = new UpsertEngineConfigCommand
        {
            Key = "NewSetting",
            Value = "true",
            DataType = "Bool",
            IsHotReloadable = true
        };

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Created", result.message);
    }

    [Fact]
    public async Task Handler_Should_Update_Existing_Config_When_Key_Found()
    {
        // Arrange
        var existingConfig = new EngineConfig
        {
            Id = 5,
            Key = "MaxOrderSize",
            Value = "50",
            IsDeleted = false
        };

        var configs = new List<EngineConfig> { existingConfig }.AsQueryable().BuildMockDbSet();
        var mockDbContext = new Mock<DbContext>();
        mockDbContext.Setup(c => c.Set<EngineConfig>()).Returns(configs.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var handler = new UpsertEngineConfigCommandHandler(_mockWriteContext.Object);
        var command = new UpsertEngineConfigCommand
        {
            Key = "MaxOrderSize",
            Value = "100",
            DataType = "Int",
            IsHotReloadable = true
        };

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Updated", result.message);
        Assert.Equal(5, result.data);
    }
}
