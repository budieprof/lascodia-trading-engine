using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class IdempotencyGuardTest
{
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly DataRetentionOptions _options;

    public IdempotencyGuardTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        _options = new DataRetentionOptions { IdempotencyKeyTtlHours = 24 };
    }

    private IdempotencyGuard CreateGuard(IEnumerable<ProcessedIdempotencyKey> existingKeys)
    {
        var mockSet = existingKeys.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<ProcessedIdempotencyKey>()).Returns(mockSet.Object);

        return new IdempotencyGuard(
            _mockReadContext.Object,
            _mockWriteContext.Object,
            _options,
            Mock.Of<ILogger<IdempotencyGuard>>());
    }

    [Fact]
    public async Task CheckAsync_NewKey_ReturnsNotProcessed()
    {
        var guard = CreateGuard(Enumerable.Empty<ProcessedIdempotencyKey>());

        var result = await guard.CheckAsync("new-key-123", CancellationToken.None);

        Assert.False(result.AlreadyProcessed);
        Assert.Null(result.CachedStatusCode);
        Assert.Null(result.CachedResponseJson);
    }

    [Fact]
    public async Task CheckAsync_ExistingNonExpiredKey_ReturnsCachedResponse()
    {
        var existingKey = new ProcessedIdempotencyKey
        {
            Id = 1,
            Key = "existing-key-456",
            Endpoint = "/ea/heartbeat",
            ResponseStatusCode = 200,
            ResponseBodyJson = "{\"status\":\"ok\"}",
            ProcessedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTime.UtcNow.AddHours(23),
            IsDeleted = false
        };

        var guard = CreateGuard(new[] { existingKey });

        var result = await guard.CheckAsync("existing-key-456", CancellationToken.None);

        Assert.True(result.AlreadyProcessed);
        Assert.Equal(200, result.CachedStatusCode);
        Assert.Equal("{\"status\":\"ok\"}", result.CachedResponseJson);
    }

    [Fact]
    public async Task CheckAsync_EmptyKey_ReturnsNotProcessed()
    {
        var guard = CreateGuard(Enumerable.Empty<ProcessedIdempotencyKey>());

        var resultEmpty = await guard.CheckAsync(string.Empty, CancellationToken.None);
        Assert.False(resultEmpty.AlreadyProcessed);

        var resultNull = await guard.CheckAsync(null!, CancellationToken.None);
        Assert.False(resultNull.AlreadyProcessed);
    }
}
