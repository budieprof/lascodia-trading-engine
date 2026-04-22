using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class SignalAbTestCoordinatorTests
{
    [Fact]
    public async Task StartAbTestAsync_RejectsSecondActiveTestForSameSymbolTimeframe()
    {
        await using var db = CreateDbContext();
        var coordinator = new SignalAbTestCoordinator(
            NullLogger<SignalAbTestCoordinator>.Instance);

        var firstId = await coordinator.StartAbTestAsync(
            1,
            2,
            "EURUSD",
            Timeframe.H1,
            db,
            db,
            maxConcurrentPerSymbol: 3);

        var secondId = await coordinator.StartAbTestAsync(
            1,
            3,
            "EURUSD",
            Timeframe.H1,
            db,
            db,
            maxConcurrentPerSymbol: 3);

        Assert.True(firstId > 0);
        Assert.Equal(-1, secondId);
    }

    [Fact]
    public async Task StartAbTestAsync_AllowsSameSymbolOnDifferentTimeframeWithinLimit()
    {
        await using var db = CreateDbContext();
        var coordinator = new SignalAbTestCoordinator(
            NullLogger<SignalAbTestCoordinator>.Instance);

        var h1Id = await coordinator.StartAbTestAsync(
            1,
            2,
            "EURUSD",
            Timeframe.H1,
            db,
            db,
            maxConcurrentPerSymbol: 3);

        var m15Id = await coordinator.StartAbTestAsync(
            4,
            5,
            "EURUSD",
            Timeframe.M15,
            db,
            db,
            maxConcurrentPerSymbol: 3);

        Assert.True(h1Id > 0);
        Assert.True(m15Id > 0);
    }

    [Fact]
    public void Evaluate_UsesConfiguredEconomicEffectAndRobustPnl()
    {
        var coordinator = new SignalAbTestCoordinator(
            NullLogger<SignalAbTestCoordinator>.Instance);
        var champion = Enumerable.Range(0, 30)
            .Select(i => Outcome(i % 2 == 0 ? 0 : 2))
            .ToList();
        var challenger = Enumerable.Range(0, 30)
            .Select(i => Outcome(i % 2 == 0 ? 1 : 3))
            .ToList();
        var state = new AbTestState(
            1,
            10,
            20,
            "EURUSD",
            Timeframe.H1,
            DateTime.UtcNow.AddDays(-1),
            champion,
            challenger);

        var defaultResult = coordinator.Evaluate(state);
        var strictEconomicResult = coordinator.Evaluate(
            state,
            options: new AbTestEvaluationOptions(MinimumEffectPnl: 10));

        Assert.Equal(AbTestDecision.PromoteChallenger, defaultResult.Decision);
        Assert.Equal(AbTestDecision.KeepChampion, strictEconomicResult.Decision);
    }

    private static AbTestOutcome Outcome(double pnl)
        => new(pnl, Math.Abs(pnl), 60, DateTime.UtcNow);

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }
}
