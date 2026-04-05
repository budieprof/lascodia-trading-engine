namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Small serializable PRNG used by optimization surrogates so search state can be
/// checkpointed and resumed exactly across worker restarts.
/// </summary>
internal sealed class DeterministicRandom
{
    private ulong _state;

    internal DeterministicRandom(int seed)
        : this(SeedFromInt(seed))
    {
    }

    internal DeterministicRandom(ulong state)
    {
        _state = state == 0 ? 0x9E3779B97F4A7C15UL : state;
    }

    internal ulong State => _state;

    internal double NextDouble()
    {
        // Keep 53 high-quality bits for IEEE double mantissa precision.
        return (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    internal int Next(int exclusiveMax)
    {
        if (exclusiveMax <= 0)
            throw new ArgumentOutOfRangeException(nameof(exclusiveMax));

        return (int)(NextUInt64() % (uint)exclusiveMax);
    }

    internal int Next(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));

        return minInclusive + Next(maxExclusive - minInclusive);
    }

    private ulong NextUInt64()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return x * 2685821657736338717UL;
    }

    private static ulong SeedFromInt(int seed)
    {
        ulong z = ((ulong)(uint)seed + 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return z == 0 ? 0xD1B54A32D192ED03UL : z;
    }
}
