using FluentValidation.TestHelper;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveTickBatch;

namespace LascodiaTradingEngine.UnitTest.Application.ExpertAdvisor;

public class ReceiveTickBatchCommandValidatorTest
{
    private readonly ReceiveTickBatchCommandValidator _validator = new();

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
    public void Empty_ticks_should_fail()
    {
        var command = CreateValidCommand();
        command.Ticks = new();
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Ticks);
    }

    [Fact]
    public void Exceeding_5000_ticks_should_fail()
    {
        var command = CreateValidCommand();
        command.Ticks = Enumerable.Range(0, 5001).Select(i => new TickItem
        {
            Symbol = "EURUSD", Bid = 1.1000m, Ask = 1.1001m, Timestamp = DateTime.UtcNow
        }).ToList();
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Ticks);
    }

    [Fact]
    public void Tick_with_zero_bid_should_fail()
    {
        var command = CreateValidCommand();
        command.Ticks[0].Bid = 0;
        var result = _validator.TestValidate(command);
        result.ShouldHaveAnyValidationError();
    }

    [Fact]
    public void Tick_with_ask_less_than_bid_should_fail()
    {
        var command = CreateValidCommand();
        command.Ticks[0].Bid = 1.1002m;
        command.Ticks[0].Ask = 1.1000m;
        var result = _validator.TestValidate(command);
        result.ShouldHaveAnyValidationError();
    }

    [Fact]
    public void Tick_with_empty_symbol_should_fail()
    {
        var command = CreateValidCommand();
        command.Ticks[0].Symbol = "";
        var result = _validator.TestValidate(command);
        result.ShouldHaveAnyValidationError();
    }

    private static ReceiveTickBatchCommand CreateValidCommand() => new()
    {
        InstanceId = "ea-instance-1",
        Ticks =
        [
            new TickItem { Symbol = "EURUSD", Bid = 1.1000m, Ask = 1.1001m, Timestamp = DateTime.UtcNow },
            new TickItem { Symbol = "GBPUSD", Bid = 1.2700m, Ask = 1.2701m, Timestamp = DateTime.UtcNow },
        ]
    };
}
