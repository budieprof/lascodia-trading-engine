using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class TransactionCostAnalyzerTest
{
    private readonly TransactionCostAnalyzer _analyzer;

    public TransactionCostAnalyzerTest()
    {
        _analyzer = new TransactionCostAnalyzer(Mock.Of<ILogger<TransactionCostAnalyzer>>());
    }

    [Fact]
    public async Task AnalyzeAsync_BuyOrder_PositiveShortfallWhenFillHigher()
    {
        var order = EntityFactory.CreateOrder(price: 1.1000m, filledPrice: 1.1005m, orderType: OrderType.Buy);
        var signal = EntityFactory.CreateSignal(entryPrice: 1.0995m);

        var tca = await _analyzer.AnalyzeAsync(order, signal, CancellationToken.None);

        Assert.True(tca.ImplementationShortfall > 0); // Buy filled higher = cost
        Assert.True(tca.DelayCost >= 0);
        Assert.Equal(order.Id, tca.OrderId);
    }

    [Fact]
    public async Task AnalyzeAsync_SellOrder_PositiveShortfallWhenFillLower()
    {
        var order = EntityFactory.CreateOrder(price: 1.1000m, filledPrice: 1.0995m, orderType: OrderType.Sell);
        var signal = EntityFactory.CreateSignal(entryPrice: 1.1005m, direction: TradeDirection.Sell);

        var tca = await _analyzer.AnalyzeAsync(order, signal, CancellationToken.None);

        Assert.True(tca.ImplementationShortfall > 0); // Sell filled lower = cost
    }

    [Fact]
    public async Task AnalyzeAsync_NullSignal_UsesOrderPriceAsArrival()
    {
        var order = EntityFactory.CreateOrder(price: 1.1000m, filledPrice: 1.1000m);

        var tca = await _analyzer.AnalyzeAsync(order, null, CancellationToken.None);

        Assert.Equal(order.Price, tca.ArrivalPrice);
        Assert.Equal(0m, tca.ImplementationShortfall);
    }

    [Fact]
    public async Task AnalyzeAsync_OrderNotFilled_Throws()
    {
        var order = EntityFactory.CreateOrder(status: OrderStatus.Pending);
        order.FilledPrice = null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _analyzer.AnalyzeAsync(order, null, CancellationToken.None));
    }
}
