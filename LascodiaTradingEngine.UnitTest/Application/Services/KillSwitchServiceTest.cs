using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using System.Diagnostics.Metrics;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.EngineConfiguration.Commands.UpsertEngineConfig;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class KillSwitchServiceTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<IReadApplicationDbContext> _readCtx = new();
    private readonly Mock<DbContext> _db = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly EngineConfigCache _configCache;
    private readonly KillSwitchService _service;

    public KillSwitchServiceTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
        _configCache = new EngineConfigCache(_metrics, TimeProvider.System);

        _readCtx.Setup(c => c.GetDbContext()).Returns(_db.Object);
        _db.Setup(d => d.Set<EngineConfig>())
            .Returns(new List<EngineConfig>().AsQueryable().BuildMockDbSet().Object);

        _mediator.Setup(m => m.Send(It.IsAny<UpsertEngineConfigCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "OK", "00"));
        _mediator.Setup(m => m.Send(It.IsAny<LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision.LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "OK", "00"));

        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_readCtx.Object);
        provider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mediator.Object);
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        _scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        _service = new KillSwitchService(_scopeFactory.Object, _configCache, _metrics, Mock.Of<ILogger<KillSwitchService>>());
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task IsGlobalKilledAsync_WhenConfigFalseOrMissing_ReturnsFalse()
    {
        // Missing key → default false.
        Assert.False(await _service.IsGlobalKilledAsync());

        // Explicit false.
        _db.Setup(d => d.Set<EngineConfig>())
            .Returns(new List<EngineConfig>
            {
                new() { Key = KillSwitchService.GlobalKey, Value = "false", DataType = LascodiaTradingEngine.Domain.Enums.ConfigDataType.Bool },
            }.AsQueryable().BuildMockDbSet().Object);
        _configCache.InvalidateAll();
        Assert.False(await _service.IsGlobalKilledAsync());
    }

    [Fact]
    public async Task IsGlobalKilledAsync_WhenConfigTrue_ReturnsTrue()
    {
        _db.Setup(d => d.Set<EngineConfig>())
            .Returns(new List<EngineConfig>
            {
                new() { Key = KillSwitchService.GlobalKey, Value = "true", DataType = LascodiaTradingEngine.Domain.Enums.ConfigDataType.Bool },
            }.AsQueryable().BuildMockDbSet().Object);
        Assert.True(await _service.IsGlobalKilledAsync());
    }

    [Fact]
    public async Task IsStrategyKilledAsync_KeyedByStrategyId()
    {
        _db.Setup(d => d.Set<EngineConfig>())
            .Returns(new List<EngineConfig>
            {
                new() { Key = KillSwitchService.StrategyKeyPrefix + "42", Value = "true", DataType = LascodiaTradingEngine.Domain.Enums.ConfigDataType.Bool },
            }.AsQueryable().BuildMockDbSet().Object);
        Assert.True(await _service.IsStrategyKilledAsync(42));
        Assert.False(await _service.IsStrategyKilledAsync(99)); // other strategy unaffected
    }

    [Fact]
    public async Task SetGlobalAsync_DispatchesUpsertCommand_WithTrueValue()
    {
        await _service.SetGlobalAsync(true, "unit test");

        _mediator.Verify(m => m.Send(
            It.Is<UpsertEngineConfigCommand>(c => c.Key == KillSwitchService.GlobalKey && c.Value == "true"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SetStrategyAsync_DispatchesUpsertCommand_WithPerStrategyKey()
    {
        await _service.SetStrategyAsync(42, enabled: true, reason: "misbehaving");

        _mediator.Verify(m => m.Send(
            It.Is<UpsertEngineConfigCommand>(c => c.Key == KillSwitchService.StrategyKeyPrefix + "42" && c.Value == "true"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
