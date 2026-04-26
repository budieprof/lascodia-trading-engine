using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MockQueryable.Moq;
using Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLModelActivatedEventHandlerTest
{
    private readonly Mock<IWriteApplicationDbContext> _writeContext = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<ILogger<MLModelActivatedEventHandler>> _logger = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<DbContext> _dbContext = new();

    public MLModelActivatedEventHandlerTest()
    {
        _writeContext.Setup(c => c.GetDbContext()).Returns(_dbContext.Object);
        _mediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_writeContext.Object);
        provider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mediator.Object);
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        _scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
    }

    [Fact]
    public async Task Handle_ActivatedModel_PersistsGovernanceDecisionWithStructuredContext()
    {
        SetupModels(CreateModel(directionAccuracy: 0.8765m, previousChampionId: 7));
        SetupDecisionLogs();
        var handler = CreateHandler();

        await handler.Handle(CreateEvent(oldModelId: 7, directionAccuracy: 0.10m));

        _mediator.Verify(m => m.Send(
            It.Is<LogDecisionCommand>(cmd =>
                cmd.EntityType == "MLModel" &&
                cmd.EntityId == 1 &&
                cmd.DecisionType == "ModelActivated" &&
                cmd.Outcome == "Active" &&
                cmd.Source == nameof(MLModelActivatedEventHandler) &&
                cmd.Reason.Contains("EURUSD/H1 model promoted", StringComparison.Ordinal) &&
                cmd.Reason.Contains("DirectionAccuracy=87.65%", StringComparison.Ordinal) &&
                cmd.Reason.Contains("replaced model 7", StringComparison.Ordinal) &&
                HasAuditContext(cmd.ContextJson, 0.8765m, "BaggedLogistic")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_SupersededButPreviouslyActivatedModel_StillAuditsHistoricalActivation()
    {
        SetupModels(CreateModel(status: MLModelStatus.Superseded, isActive: false));
        SetupDecisionLogs();
        var handler = CreateHandler();

        await handler.Handle(CreateEvent());

        _mediator.Verify(m => m.Send(
            It.Is<LogDecisionCommand>(cmd => cmd.EntityId == 1 && cmd.DecisionType == "ModelActivated"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_DecisionLogAlreadyExists_SkipsDuplicate()
    {
        SetupModels(CreateModel());
        SetupDecisionLogs(new DecisionLog
        {
            EntityType = "MLModel",
            EntityId = 1,
            DecisionType = "ModelActivated",
            Outcome = "Active",
            Reason = "already logged",
            Source = nameof(MLModelActivatedEventHandler)
        });
        var handler = CreateHandler();

        await handler.Handle(CreateEvent());

        _mediator.Verify(
            m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_ModelNotDurablyActivated_SkipsOutOfOrderEvent()
    {
        SetupModels(CreateModel(status: MLModelStatus.Training, isActive: false, activatedAt: null));
        SetupDecisionLogs();
        var handler = CreateHandler();

        await handler.Handle(CreateEvent());

        _mediator.Verify(
            m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_EventSymbolMismatch_SkipsCorruptAudit()
    {
        SetupModels(CreateModel(symbol: "GBPUSD"));
        SetupDecisionLogs();
        var handler = CreateHandler();

        await handler.Handle(CreateEvent(symbol: "EURUSD"));

        _mediator.Verify(
            m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_MissingTrainingRunId_StillAuditsWithUnknownLabel()
    {
        SetupModels(CreateModel());
        SetupDecisionLogs();
        var handler = CreateHandler();

        await handler.Handle(CreateEvent(trainingRunId: 0));

        _mediator.Verify(m => m.Send(
            It.Is<LogDecisionCommand>(cmd =>
                cmd.EntityId == 1 &&
                cmd.Reason.Contains("training run unknown", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_InvalidEnvelope_SkipsBeforeCreatingScope()
    {
        var handler = CreateHandler();

        await handler.Handle(CreateEvent(newModelId: 0));

        _scopeFactory.Verify(f => f.CreateScope(), Times.Never);
        _mediator.Verify(
            m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_WhenAuditLockIsBusy_ThrowsForEventRedelivery()
    {
        var lockProvider = new Mock<IDistributedLock>();
        lockProvider
            .Setup(l => l.TryAcquireAsync(
                "ml:model-activation-audit:1",
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);
        var handler = CreateHandler(lockProvider.Object);

        await Assert.ThrowsAsync<TimeoutException>(() => handler.Handle(CreateEvent()));

        _scopeFactory.Verify(f => f.CreateScope(), Times.Never);
    }

    private MLModelActivatedEventHandler CreateHandler(IDistributedLock? distributedLock = null)
        => new(_scopeFactory.Object, _logger.Object, distributedLock);

    private void SetupModels(params MLModel[] models)
    {
        var set = models.AsQueryable().BuildMockDbSet();
        _dbContext.Setup(c => c.Set<MLModel>()).Returns(set.Object);
    }

    private void SetupDecisionLogs(params DecisionLog[] logs)
    {
        var set = logs.AsQueryable().BuildMockDbSet();
        _dbContext.Setup(c => c.Set<DecisionLog>()).Returns(set.Object);
    }

    private static MLModel CreateModel(
        long id = 1,
        string symbol = "EURUSD",
        Timeframe timeframe = Timeframe.H1,
        MLModelStatus status = MLModelStatus.Active,
        bool isActive = true,
        DateTime? activatedAt = null,
        decimal? directionAccuracy = 0.75m,
        long? previousChampionId = null)
        => new()
        {
            Id = id,
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = "EURUSD_H1_42_20260426120000",
            Status = status,
            IsActive = isActive,
            ActivatedAt = activatedAt ?? new DateTime(2026, 04, 26, 12, 0, 0, DateTimeKind.Utc),
            DirectionAccuracy = directionAccuracy,
            TrainingSamples = 500,
            LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            PreviousChampionModelId = previousChampionId,
            IsDeleted = false
        };

    private static MLModelActivatedIntegrationEvent CreateEvent(
        long newModelId = 1,
        long? oldModelId = null,
        string symbol = "EURUSD",
        Timeframe timeframe = Timeframe.H1,
        long trainingRunId = 42,
        decimal directionAccuracy = 0.75m)
        => new()
        {
            NewModelId = newModelId,
            OldModelId = oldModelId,
            Symbol = symbol,
            Timeframe = timeframe,
            TrainingRunId = trainingRunId,
            DirectionAccuracy = directionAccuracy,
            ActivatedAt = new DateTime(2026, 04, 26, 12, 0, 0, DateTimeKind.Utc)
        };

    private static bool HasAuditContext(
        string? contextJson,
        decimal expectedAccuracy,
        string expectedArchitecture)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return false;

        using var document = JsonDocument.Parse(contextJson);
        var root = document.RootElement;

        return root.GetProperty("modelId").GetInt64() == 1 &&
               root.GetProperty("auditDirectionAccuracy").GetDecimal() == expectedAccuracy &&
               root.GetProperty("learnerArchitecture").GetString() == expectedArchitecture &&
               root.GetProperty("previousChampionModelId").GetInt64() == 7;
    }
}
