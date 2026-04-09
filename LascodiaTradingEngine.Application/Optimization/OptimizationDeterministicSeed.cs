using System.Security.Cryptography;
using System.Text;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationDeterministicSeed
{
    internal const int Version = 1;
    private const string VersionTag = "optimization-seed-v1";

    internal static int Compute(long runId, long strategyId, DateTime queueAnchorUtc)
    {
        var payload = $"{VersionTag}|{runId}|{strategyId}|{queueAnchorUtc:O}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var seed = BitConverter.ToInt32(hash, 0) & int.MaxValue;
        return seed == 0 ? 1 : seed;
    }
}
