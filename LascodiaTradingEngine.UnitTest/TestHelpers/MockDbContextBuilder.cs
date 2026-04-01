using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.UnitTest.TestHelpers;

/// <summary>Builds mock read/write DB contexts with pre-configured entity sets.</summary>
public class MockDbContextBuilder
{
    private readonly Mock<DbContext> _mockDbContext = new();

    public Mock<DbContext> DbContext => _mockDbContext;

    public MockDbContextBuilder WithSet<T>(IEnumerable<T> data) where T : class
    {
        var mockSet = data.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<T>()).Returns(mockSet.Object);
        return this;
    }

    public MockDbContextBuilder WithEmptySet<T>() where T : class
        => WithSet(Enumerable.Empty<T>());

    public Mock<IReadApplicationDbContext> BuildReadContext()
    {
        var mock = new Mock<IReadApplicationDbContext>();
        mock.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        return mock;
    }

    public Mock<IWriteApplicationDbContext> BuildWriteContext()
    {
        var mock = new Mock<IWriteApplicationDbContext>();
        mock.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        return mock;
    }
}
