using LascodiaTradingEngine.Application.Services.RateLimiting;

namespace LascodiaTradingEngine.UnitTest.Application.RateLimiting;

public class TokenBucketRateLimiterTest
{
    private readonly TokenBucketRateLimiter _limiter = new();

    [Fact]
    public async Task TryAcquire_FirstRequest_Allowed()
    {
        var result = await _limiter.TryAcquireAsync("test-key", 5, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task TryAcquire_UnderLimit_AllAllowed()
    {
        for (int i = 0; i < 5; i++)
        {
            var result = await _limiter.TryAcquireAsync("under-limit", 5, TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.True(result);
        }
    }

    [Fact]
    public async Task TryAcquire_OverLimit_Blocked()
    {
        for (int i = 0; i < 3; i++)
            await _limiter.TryAcquireAsync("over-limit", 3, TimeSpan.FromMinutes(1), CancellationToken.None);

        var result = await _limiter.TryAcquireAsync("over-limit", 3, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task TryAcquire_DifferentKeys_Independent()
    {
        for (int i = 0; i < 3; i++)
            await _limiter.TryAcquireAsync("key-a", 3, TimeSpan.FromMinutes(1), CancellationToken.None);

        // key-a is exhausted, but key-b should still work
        var result = await _limiter.TryAcquireAsync("key-b", 3, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task GetRemaining_NewKey_ReturnsMax()
    {
        var remaining = await _limiter.GetRemainingAsync("fresh-key", 10, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(10, remaining);
    }

    [Fact]
    public async Task GetRemaining_AfterRequests_ReturnsCorrectCount()
    {
        await _limiter.TryAcquireAsync("counted-key", 10, TimeSpan.FromMinutes(1), CancellationToken.None);
        await _limiter.TryAcquireAsync("counted-key", 10, TimeSpan.FromMinutes(1), CancellationToken.None);

        var remaining = await _limiter.GetRemainingAsync("counted-key", 10, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(8, remaining);
    }

    [Fact]
    public async Task GetRemaining_Exhausted_ReturnsZero()
    {
        for (int i = 0; i < 3; i++)
            await _limiter.TryAcquireAsync("exhausted", 3, TimeSpan.FromMinutes(1), CancellationToken.None);

        var remaining = await _limiter.GetRemainingAsync("exhausted", 3, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(0, remaining);
    }
}
