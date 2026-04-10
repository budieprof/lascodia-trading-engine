namespace LascodiaTradingEngine.Application.Optimization;

internal sealed class OptimizationLeaseOwnershipChangedException : Exception
{
    public OptimizationLeaseOwnershipChangedException(long runId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        RunId = runId;
    }

    public long RunId { get; }
}
