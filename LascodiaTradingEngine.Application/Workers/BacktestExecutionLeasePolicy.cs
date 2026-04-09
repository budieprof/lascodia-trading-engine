namespace LascodiaTradingEngine.Application.Workers;

internal static class BacktestExecutionLeasePolicy
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    internal static TimeSpan GetHeartbeatInterval()
        => TimeSpan.FromSeconds(30);
}
