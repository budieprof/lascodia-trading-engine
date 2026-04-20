using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.AcknowledgeCommand;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.ExpertAdvisor;

public class AcknowledgeCommandIdempotencyTests
{
    [Fact]
    public async Task FirstAck_RecordsClientAckToken_AndFinalizes()
    {
        var ea = new EACommand { Id = 1, TargetInstanceId = "ea1", Symbol = "EURUSD" };
        var handler = NewHandler(ea);

        var result = await handler.Handle(new AcknowledgeCommandCommand
        {
            Id = 1,
            Status = "Success",
            Result = "OK",
            ClientAckToken = "token-A",
        }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.True(ea.Acknowledged);
        Assert.Equal("token-A", ea.ClientAckToken);
    }

    [Fact]
    public async Task ReplayWithSameToken_ReturnsIdempotentSuccess_NotConflict()
    {
        // Simulate the first ACK having already happened: Acknowledged=true + token set.
        var ea = new EACommand
        {
            Id = 1,
            TargetInstanceId = "ea1",
            Symbol = "EURUSD",
            Acknowledged = true,
            AcknowledgedAt = DateTime.UtcNow.AddSeconds(-5),
            AckResult = "OK",
            ClientAckToken = "token-A",
        };
        var handler = NewHandler(ea);

        var result = await handler.Handle(new AcknowledgeCommandCommand
        {
            Id = 1,
            Status = "Success",
            Result = "OK",
            ClientAckToken = "token-A",
        }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("OK", result.data);
    }

    [Fact]
    public async Task ReplayWithDifferentToken_Returns409Conflict()
    {
        var ea = new EACommand
        {
            Id = 1,
            TargetInstanceId = "ea1",
            Symbol = "EURUSD",
            Acknowledged = true,
            ClientAckToken = "token-A",
        };
        var handler = NewHandler(ea);

        var result = await handler.Handle(new AcknowledgeCommandCommand
        {
            Id = 1,
            Status = "Success",
            ClientAckToken = "token-B", // different execution, different token
        }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-409", result.responseCode);
    }

    [Fact]
    public async Task MissingTokenOnReplay_FallsBackToLegacy409()
    {
        // Backwards-compat: clients that don't supply a token see the legacy
        // "already acknowledged" 409 — matches existing behaviour.
        var ea = new EACommand { Id = 1, Symbol = "EURUSD", Acknowledged = true };
        var handler = NewHandler(ea);

        var result = await handler.Handle(new AcknowledgeCommandCommand { Id = 1, Status = "Success" },
            CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-409", result.responseCode);
    }

    private static AcknowledgeCommandCommandHandler NewHandler(EACommand entity)
    {
        var mockWrite = new Mock<IWriteApplicationDbContext>();
        var mockDb = new Mock<DbContext>();
        mockWrite.Setup(w => w.GetDbContext()).Returns(mockDb.Object);
        mockWrite.Setup(w => w.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mockDb.Setup(d => d.Set<EACommand>())
            .Returns(new List<EACommand> { entity }.AsQueryable().BuildMockDbSet().Object);
        return new AcknowledgeCommandCommandHandler(mockWrite.Object);
    }
}
