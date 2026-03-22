using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Domain;

public class OrderStatusTransitionsTest
{
    // --- OrderStatus: valid transitions ---

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Submitted)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Pending, OrderStatus.Rejected)]
    [InlineData(OrderStatus.Pending, OrderStatus.Expired)]
    [InlineData(OrderStatus.Submitted, OrderStatus.PartialFill)]
    [InlineData(OrderStatus.Submitted, OrderStatus.Filled)]
    [InlineData(OrderStatus.Submitted, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Submitted, OrderStatus.Rejected)]
    [InlineData(OrderStatus.Submitted, OrderStatus.Expired)]
    [InlineData(OrderStatus.PartialFill, OrderStatus.Filled)]
    [InlineData(OrderStatus.PartialFill, OrderStatus.Cancelled)]
    public void CanTransitionTo_ValidOrderTransition_ReturnsTrue(OrderStatus from, OrderStatus to)
    {
        Assert.True(from.CanTransitionTo(to));
    }

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Submitted)]
    [InlineData(OrderStatus.Submitted, OrderStatus.Filled)]
    [InlineData(OrderStatus.PartialFill, OrderStatus.Filled)]
    public void EnsureTransition_ValidOrderTransition_DoesNotThrow(OrderStatus from, OrderStatus to)
    {
        var exception = Record.Exception(() => from.EnsureTransition(to));
        Assert.Null(exception);
    }

    // --- OrderStatus: invalid transitions ---

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Filled)]
    [InlineData(OrderStatus.Pending, OrderStatus.PartialFill)]
    [InlineData(OrderStatus.PartialFill, OrderStatus.Submitted)]
    [InlineData(OrderStatus.PartialFill, OrderStatus.Pending)]
    [InlineData(OrderStatus.Submitted, OrderStatus.Pending)]
    public void CanTransitionTo_InvalidOrderTransition_ReturnsFalse(OrderStatus from, OrderStatus to)
    {
        Assert.False(from.CanTransitionTo(to));
    }

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Filled)]
    [InlineData(OrderStatus.Filled, OrderStatus.Pending)]
    public void EnsureTransition_InvalidOrderTransition_ThrowsInvalidOperationException(OrderStatus from, OrderStatus to)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => from.EnsureTransition(to));
        Assert.Contains(from.ToString(), ex.Message);
        Assert.Contains(to.ToString(), ex.Message);
    }

    // --- OrderStatus: terminal states cannot transition to anything ---

    [Theory]
    [InlineData(OrderStatus.Filled)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Expired)]
    public void CanTransitionTo_TerminalOrderStatus_ReturnsFalseForAllTargets(OrderStatus terminal)
    {
        foreach (OrderStatus target in Enum.GetValues<OrderStatus>())
        {
            Assert.False(terminal.CanTransitionTo(target),
                $"Terminal status {terminal} should not transition to {target}");
        }
    }

    [Theory]
    [InlineData(OrderStatus.Filled)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Expired)]
    public void EnsureTransition_TerminalOrderStatus_ThrowsForAnyTarget(OrderStatus terminal)
    {
        foreach (OrderStatus target in Enum.GetValues<OrderStatus>())
        {
            Assert.Throws<InvalidOperationException>(() => terminal.EnsureTransition(target));
        }
    }

    // --- OrderStatus: self-transition is never allowed ---

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Submitted)]
    [InlineData(OrderStatus.PartialFill)]
    [InlineData(OrderStatus.Filled)]
    public void CanTransitionTo_SameOrderStatus_ReturnsFalse(OrderStatus status)
    {
        Assert.False(status.CanTransitionTo(status));
    }

    // --- StrategyStatus: valid transitions ---

    [Theory]
    [InlineData(StrategyStatus.Active, StrategyStatus.Paused)]
    [InlineData(StrategyStatus.Active, StrategyStatus.Stopped)]
    [InlineData(StrategyStatus.Paused, StrategyStatus.Active)]
    [InlineData(StrategyStatus.Paused, StrategyStatus.Stopped)]
    [InlineData(StrategyStatus.Paused, StrategyStatus.Backtesting)]
    [InlineData(StrategyStatus.Backtesting, StrategyStatus.Paused)]
    [InlineData(StrategyStatus.Backtesting, StrategyStatus.Stopped)]
    [InlineData(StrategyStatus.Stopped, StrategyStatus.Paused)]
    public void CanTransitionTo_ValidStrategyTransition_ReturnsTrue(StrategyStatus from, StrategyStatus to)
    {
        Assert.True(from.CanTransitionTo(to));
    }

    [Theory]
    [InlineData(StrategyStatus.Active, StrategyStatus.Paused)]
    [InlineData(StrategyStatus.Stopped, StrategyStatus.Paused)]
    public void EnsureTransition_ValidStrategyTransition_DoesNotThrow(StrategyStatus from, StrategyStatus to)
    {
        var exception = Record.Exception(() => from.EnsureTransition(to));
        Assert.Null(exception);
    }

    // --- StrategyStatus: invalid transitions ---

    [Theory]
    [InlineData(StrategyStatus.Active, StrategyStatus.Backtesting)]
    [InlineData(StrategyStatus.Stopped, StrategyStatus.Active)]
    [InlineData(StrategyStatus.Stopped, StrategyStatus.Backtesting)]
    [InlineData(StrategyStatus.Backtesting, StrategyStatus.Active)]
    public void CanTransitionTo_InvalidStrategyTransition_ReturnsFalse(StrategyStatus from, StrategyStatus to)
    {
        Assert.False(from.CanTransitionTo(to));
    }

    [Theory]
    [InlineData(StrategyStatus.Active, StrategyStatus.Backtesting)]
    [InlineData(StrategyStatus.Stopped, StrategyStatus.Active)]
    public void EnsureTransition_InvalidStrategyTransition_ThrowsInvalidOperationException(StrategyStatus from, StrategyStatus to)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => from.EnsureTransition(to));
        Assert.Contains(from.ToString(), ex.Message);
        Assert.Contains(to.ToString(), ex.Message);
    }

    // --- StrategyStatus: self-transition is never allowed ---

    [Theory]
    [InlineData(StrategyStatus.Active)]
    [InlineData(StrategyStatus.Paused)]
    [InlineData(StrategyStatus.Backtesting)]
    [InlineData(StrategyStatus.Stopped)]
    public void CanTransitionTo_SameStrategyStatus_ReturnsFalse(StrategyStatus status)
    {
        Assert.False(status.CanTransitionTo(status));
    }
}
