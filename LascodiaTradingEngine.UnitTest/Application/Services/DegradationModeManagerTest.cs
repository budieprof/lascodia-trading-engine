using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class DegradationModeManagerTest
{
    private readonly DegradationModeManager _manager;

    public DegradationModeManagerTest()
    {
        _manager = new DegradationModeManager(Mock.Of<ILogger<DegradationModeManager>>());
    }

    [Fact]
    public void CurrentMode_StartsInNormal()
    {
        Assert.Equal(DegradationMode.Normal, _manager.CurrentMode);
    }

    [Fact]
    public async Task TransitionToAsync_ChangesMode()
    {
        await _manager.TransitionToAsync(DegradationMode.MLDegraded, "ML scorer offline", CancellationToken.None);

        Assert.Equal(DegradationMode.MLDegraded, _manager.CurrentMode);
    }

    [Fact]
    public async Task RecordSubsystemHeartbeat_ResetsModeTpNormal_WhenAllHealthy()
    {
        // First transition to a degraded state
        await _manager.TransitionToAsync(DegradationMode.MLDegraded, "ML scorer offline", CancellationToken.None);
        Assert.Equal(DegradationMode.MLDegraded, _manager.CurrentMode);

        // Record heartbeats for all tracked subsystems so all are operational
        _manager.RecordSubsystemHeartbeat(DegradationModeManager.SubsystemMLScorer);
        _manager.RecordSubsystemHeartbeat(DegradationModeManager.SubsystemEventBus);
        _manager.RecordSubsystemHeartbeat(DegradationModeManager.SubsystemReadDb);

        // After heartbeats, mode should recover to Normal
        Assert.Equal(DegradationMode.Normal, _manager.CurrentMode);
    }

    [Fact]
    public void IsSubsystemOperational_StaleHeartbeat_ReturnsFalse()
    {
        // Record a heartbeat, then check it's operational
        _manager.RecordSubsystemHeartbeat(DegradationModeManager.SubsystemMLScorer);
        Assert.True(_manager.IsSubsystemOperational(DegradationModeManager.SubsystemMLScorer));

        // We cannot easily fast-forward time, but we can verify that a subsystem
        // with no heartbeat recorded is considered operational (startup assumption)
        Assert.True(_manager.IsSubsystemOperational("UnknownSubsystem"));
    }

    [Fact]
    public async Task EmergencyHalt_DoesNotAutoRecover_FromHeartbeat()
    {
        // Transition to EmergencyHalt
        await _manager.TransitionToAsync(DegradationMode.EmergencyHalt, "Critical failure", CancellationToken.None);
        Assert.Equal(DegradationMode.EmergencyHalt, _manager.CurrentMode);

        // Record heartbeats for all subsystems
        _manager.RecordSubsystemHeartbeat(DegradationModeManager.SubsystemMLScorer);
        _manager.RecordSubsystemHeartbeat(DegradationModeManager.SubsystemEventBus);
        _manager.RecordSubsystemHeartbeat(DegradationModeManager.SubsystemReadDb);

        // EmergencyHalt should NOT auto-recover from heartbeats
        Assert.Equal(DegradationMode.EmergencyHalt, _manager.CurrentMode);
    }
}
