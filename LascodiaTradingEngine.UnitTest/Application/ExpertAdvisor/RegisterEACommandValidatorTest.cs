using FluentValidation.TestHelper;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.RegisterEA;

namespace LascodiaTradingEngine.UnitTest.Application.ExpertAdvisor;

public class RegisterEACommandValidatorTest
{
    private readonly RegisterEACommandValidator _validator = new();

    [Fact]
    public void Valid_command_should_pass()
    {
        var command = CreateValidCommand();
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Empty_instanceId_should_fail()
    {
        var command = CreateValidCommand();
        command.InstanceId = "";
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.InstanceId);
    }

    [Fact]
    public void InstanceId_exceeding_64_chars_should_fail()
    {
        var command = CreateValidCommand();
        command.InstanceId = new string('a', 65);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.InstanceId);
    }

    [Fact]
    public void Zero_tradingAccountId_should_fail()
    {
        var command = CreateValidCommand();
        command.TradingAccountId = 0;
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.TradingAccountId);
    }

    [Fact]
    public void Empty_symbols_should_fail()
    {
        var command = CreateValidCommand();
        command.Symbols = "";
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Symbols);
    }

    [Fact]
    public void Empty_chartSymbol_should_fail()
    {
        var command = CreateValidCommand();
        command.ChartSymbol = "";
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.ChartSymbol);
    }

    private static RegisterEACommand CreateValidCommand() => new()
    {
        InstanceId       = "ea-instance-1",
        TradingAccountId = 1,
        Symbols          = "EURUSD,GBPUSD",
        ChartSymbol      = "EURUSD",
        ChartTimeframe   = "H1",
        EAVersion        = "1.0.0"
    };
}
