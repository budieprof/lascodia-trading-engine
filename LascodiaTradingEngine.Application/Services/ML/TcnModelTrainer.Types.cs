using System.Collections.Concurrent;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TcnModelTrainer
{
    // ── Buffer Pooling (Item 32/33) ─────────────────────────────────────────

    /// <summary>
    /// Pre-allocated inference buffer containing reusable arrays for
    /// <see cref="CausalConvForwardFull"/> and <see cref="CausalConvForwardWithAttention"/>.
    /// Eliminates per-call jagged <c>double[][]</c> allocations on the inference hot path.
    /// </summary>
    internal sealed class TcnInferenceBuffer
    {
        /// <summary>Primary activation buffer [seqT][maxChannels].</summary>
        public double[][] BufA { get; }

        /// <summary>Secondary activation buffer [seqT][maxChannels].</summary>
        public double[][] BufB { get; }

        /// <summary>Scratch row for pre-activation values [maxChannels].</summary>
        public double[] PreActRow { get; }

        /// <summary>Number of timesteps this buffer was allocated for.</summary>
        public int SeqT { get; }

        /// <summary>Maximum channel width this buffer supports.</summary>
        public int MaxChannels { get; }

        /// <summary>
        /// Allocates all arrays for the given dimensions.
        /// </summary>
        /// <param name="seqT">Number of timesteps.</param>
        /// <param name="maxChannels">Maximum channel count across all blocks.</param>
        /// <param name="filters">Filter count (used to size the pre-activation row).</param>
        public TcnInferenceBuffer(int seqT, int maxChannels, int filters)
        {
            SeqT = seqT;
            MaxChannels = maxChannels;

            BufA = new double[seqT][];
            BufB = new double[seqT][];
            for (int t = 0; t < seqT; t++)
            {
                BufA[t] = new double[maxChannels];
                BufB[t] = new double[maxChannels];
            }

            PreActRow = new double[Math.Max(maxChannels, filters)];
        }
    }

    /// <summary>
    /// Thread-safe pool of <see cref="TcnInferenceBuffer"/> instances.
    /// Buffers are rented for the duration of a forward pass and returned
    /// afterwards to avoid repeated allocations during inference.
    /// </summary>
    internal sealed class TcnInferenceBufferPool
    {
        private readonly ConcurrentBag<TcnInferenceBuffer> _pool = new();

        /// <summary>
        /// Rents a buffer that is at least as large as the requested dimensions.
        /// If no suitable buffer is available, a new one is allocated.
        /// </summary>
        /// <param name="seqT">Required number of timesteps.</param>
        /// <param name="maxChannels">Required maximum channel width.</param>
        /// <param name="filters">Required filter count.</param>
        /// <returns>A reusable <see cref="TcnInferenceBuffer"/>.</returns>
        public TcnInferenceBuffer Rent(int seqT, int maxChannels, int filters)
        {
            if (_pool.TryTake(out var buffer)
                && buffer.SeqT >= seqT
                && buffer.MaxChannels >= maxChannels
                && buffer.PreActRow.Length >= Math.Max(maxChannels, filters))
            {
                // Clear residual data from previous use.
                for (int t = 0; t < buffer.SeqT; t++)
                {
                    Array.Clear(buffer.BufA[t]);
                    Array.Clear(buffer.BufB[t]);
                }

                Array.Clear(buffer.PreActRow);
                return buffer;
            }

            // Buffer was too small or bag was empty — allocate a fresh one.
            // If we popped a too-small buffer, let it be GC'd.
            return new TcnInferenceBuffer(seqT, maxChannels, filters);
        }

        /// <summary>
        /// Returns a buffer to the pool for future reuse.
        /// </summary>
        /// <param name="buffer">The buffer to return.</param>
        public void Return(TcnInferenceBuffer buffer)
        {
            _pool.Add(buffer);
        }
    }

    // ── Flat Activation Buffer (Item 32) ────────────────────────────────────

    /// <summary>
    /// Flat buffer layout wrapping a contiguous <c>double[]</c> that provides
    /// <c>[block, t, channel]</c> indexing. Eliminates jagged array overhead
    /// and improves cache locality for sequential timestep access.
    /// </summary>
    internal readonly struct FlatActivationBuffer
    {
        private readonly double[] _data;
        private readonly int _timeSteps;
        private readonly int _channels;

        /// <summary>Total number of blocks in this buffer.</summary>
        public int Blocks { get; }

        /// <summary>
        /// Allocates a contiguous buffer for the given 3-D dimensions.
        /// </summary>
        /// <param name="blocks">Number of TCN blocks.</param>
        /// <param name="timeSteps">Number of timesteps per block.</param>
        /// <param name="channels">Number of channels per timestep.</param>
        public FlatActivationBuffer(int blocks, int timeSteps, int channels)
        {
            Blocks = blocks;
            _timeSteps = timeSteps;
            _channels = channels;
            _data = new double[blocks * timeSteps * channels];
        }

        /// <summary>
        /// Provides ref access to a single element at <c>[block, t, channel]</c>.
        /// </summary>
        public ref double this[int block, int t, int channel]
            => ref _data[(block * _timeSteps + t) * _channels + channel];

        /// <summary>
        /// Returns a <see cref="Span{T}"/> over all channels for one (block, timestep) pair.
        /// Useful for vectorised per-timestep operations.
        /// </summary>
        /// <param name="block">Block index.</param>
        /// <param name="t">Timestep index.</param>
        /// <returns>A span of length <c>channels</c>.</returns>
        public Span<double> GetTimestepSpan(int block, int t)
            => _data.AsSpan((block * _timeSteps + t) * _channels, _channels);

        /// <summary>
        /// Zeroes the entire buffer.
        /// </summary>
        public void Clear() => Array.Clear(_data);
    }

    // ── Kahan Compensated Summation (Item 30) ───────────────────────────────

    /// <summary>
    /// Kahan compensated summation accumulator that prevents floating-point drift
    /// when summing many small values (e.g. gradient accumulation across mini-batches).
    /// </summary>
    internal struct KahanAccumulator
    {
        private double _sum;
        private double _compensation;

        /// <summary>
        /// Adds a value using Kahan compensated summation.
        /// </summary>
        /// <param name="value">The value to add.</param>
        public void Add(double value)
        {
            double y = value - _compensation;
            double t = _sum + y;
            _compensation = (t - _sum) - y;
            _sum = t;
        }

        /// <summary>
        /// The compensated running sum.
        /// </summary>
        public readonly double Sum => _sum;

        /// <summary>
        /// Resets the accumulator to zero.
        /// </summary>
        public void Reset()
        {
            _sum = 0;
            _compensation = 0;
        }
    }

    // ── Gradient Norm Tracker (Item 28) ─────────────────────────────────────

    /// <summary>
    /// Tracks per-block gradient norms during training to detect exploding or
    /// vanishing gradients. Records max and mean norms per block and can flag
    /// anomalous blocks whose max norm exceeds the overall mean by a configurable
    /// multiplier.
    /// </summary>
    internal sealed class GradientNormTracker
    {
        private readonly double[] _maxNorms;
        private readonly double[] _sumNorms;
        private readonly int[] _counts;

        /// <summary>
        /// Creates a tracker for the specified number of TCN blocks.
        /// </summary>
        /// <param name="numBlocks">Number of blocks to track.</param>
        public GradientNormTracker(int numBlocks)
        {
            _maxNorms = new double[numBlocks];
            _sumNorms = new double[numBlocks];
            _counts = new int[numBlocks];
        }

        /// <summary>
        /// Records a gradient norm observation for the given block.
        /// </summary>
        /// <param name="block">Block index.</param>
        /// <param name="norm">L2 norm of the gradient for that block.</param>
        public void Record(int block, double norm)
        {
            if (norm > _maxNorms[block])
                _maxNorms[block] = norm;

            _sumNorms[block] += norm;
            _counts[block]++;
        }

        /// <summary>
        /// Returns a defensive copy of per-block maximum norms.
        /// </summary>
        public double[] GetMaxNorms() => (double[])_maxNorms.Clone();

        /// <summary>
        /// Returns the mean gradient norm for the specified block,
        /// or zero if no observations have been recorded.
        /// </summary>
        /// <param name="block">Block index.</param>
        public double GetMeanNorm(int block)
            => _counts[block] > 0 ? _sumNorms[block] / _counts[block] : 0;

        /// <summary>
        /// Checks whether any block has a maximum gradient norm that exceeds
        /// the overall mean maximum norm by the specified multiplier.
        /// Useful for detecting exploding gradients in deeper blocks.
        /// </summary>
        /// <param name="thresholdMultiplier">
        /// Multiplier applied to the mean of max norms (default: 10).
        /// </param>
        /// <returns><c>true</c> if any block is anomalous; otherwise <c>false</c>.</returns>
        public bool HasAnomalousBlock(double thresholdMultiplier = 10.0)
        {
            int numBlocks = _maxNorms.Length;
            if (numBlocks == 0) return false;

            double sumMaxNorms = 0;
            for (int i = 0; i < numBlocks; i++)
                sumMaxNorms += _maxNorms[i];

            double meanMax = sumMaxNorms / numBlocks;
            double threshold = meanMax * thresholdMultiplier;

            for (int i = 0; i < numBlocks; i++)
            {
                if (_maxNorms[i] > threshold)
                    return true;
            }

            return false;
        }
    }
}
