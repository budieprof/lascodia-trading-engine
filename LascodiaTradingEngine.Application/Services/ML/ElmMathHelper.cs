using System.Numerics;
using System.Runtime.CompilerServices;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Linear algebra, SIMD-accelerated dot products, and math utilities for the ELM trainer.
/// </summary>
internal static class ElmMathHelper
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Cholesky solver
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Solves the symmetric positive-definite system A x = b via Cholesky factorization.
    /// O(n³/3) factorization + O(n²) solve.
    /// Returns false if the matrix is not positive-definite.
    /// </summary>
    internal static bool CholeskySolve(double[,] A, double[] b, double[] x, int n)
    {
        var L = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
                L[i, j] = A[i, j];

        for (int j = 0; j < n; j++)
        {
            double sum = 0;
            for (int k = 0; k < j; k++)
                sum += L[j, k] * L[j, k];

            double diag = L[j, j] - sum;
            if (diag <= 1e-15)
                return false;

            L[j, j] = Math.Sqrt(diag);
            double invDiag = 1.0 / L[j, j];

            for (int i = j + 1; i < n; i++)
            {
                double s = 0;
                for (int k = 0; k < j; k++)
                    s += L[i, k] * L[j, k];
                L[i, j] = (L[i, j] - s) * invDiag;
            }
        }

        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = b[i];
            for (int k = 0; k < i; k++)
                s -= L[i, k] * y[k];
            y[i] = s / L[i, i];
        }

        for (int i = n - 1; i >= 0; i--)
        {
            double s = y[i];
            for (int k = i + 1; k < n; k++)
                s -= L[k, i] * x[k];
            x[i] = s / L[i, i];
        }

        return true;
    }

    /// <summary>
    /// Inverts a symmetric positive-definite matrix via repeated Cholesky solves.
    /// Returns false if the matrix is not positive-definite.
    /// </summary>
    internal static bool TryInvertSpd(double[,] A, double[,] inverse, int n)
    {
        var L = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
                L[i, j] = A[i, j];

        for (int j = 0; j < n; j++)
        {
            double sum = 0;
            for (int k = 0; k < j; k++)
                sum += L[j, k] * L[j, k];

            double diag = L[j, j] - sum;
            if (diag <= 1e-15)
                return false;

            L[j, j] = Math.Sqrt(diag);
            double invDiag = 1.0 / L[j, j];

            for (int i = j + 1; i < n; i++)
            {
                double s = 0;
                for (int k = 0; k < j; k++)
                    s += L[i, k] * L[j, k];
                L[i, j] = (L[i, j] - s) * invDiag;
            }
        }

        var y = new double[n];
        var x = new double[n];
        for (int col = 0; col < n; col++)
        {
            Array.Clear(y, 0, n);
            Array.Clear(x, 0, n);

            for (int i = 0; i < n; i++)
            {
                double rhs = i == col ? 1.0 : 0.0;
                for (int k = 0; k < i; k++)
                    rhs -= L[i, k] * y[k];
                y[i] = rhs / L[i, i];
            }

            for (int i = n - 1; i >= 0; i--)
            {
                double rhs = y[i];
                for (int k = i + 1; k < n; k++)
                    rhs -= L[k, i] * x[k];
                x[i] = rhs / L[i, i];
            }

            for (int i = 0; i < n; i++)
                inverse[i, col] = x[i];
        }

        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SIMD-accelerated dot product
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the dot product of a double[] weights row and a float[] features vector
    /// using SIMD where available, falling back to scalar code otherwise.
    /// Used in the ELM hidden-layer forward pass (the hottest loop in training and inference).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double DotProductSimd(double[] weights, int weightOffset, float[] features, int[] subset, int subsetLen)
    {
        double sum = 0.0;

        if (Vector.IsHardwareAccelerated && subsetLen >= Vector<double>.Count * 2)
        {
            int vecSize = Vector<double>.Count;
            int vecEnd = subsetLen - (subsetLen % vecSize);
            var vSum = Vector<double>.Zero;
            Span<double> featureBuf = stackalloc double[vecSize];

            for (int si = 0; si < vecEnd; si += vecSize)
            {
                var vW = new Vector<double>(weights, weightOffset + si);
                for (int vi = 0; vi < vecSize; vi++)
                {
                    int fi = subset[si + vi];
                    featureBuf[vi] = fi < features.Length ? features[fi] : 0.0;
                }
                var vF = new Vector<double>(featureBuf);
                vSum += vW * vF;
            }

            for (int vi = 0; vi < vecSize; vi++)
                sum += vSum[vi];

            for (int si = vecEnd; si < subsetLen; si++)
            {
                int fi = subset[si];
                if (fi < features.Length)
                    sum += weights[weightOffset + si] * features[fi];
            }
        }
        else
        {
            for (int si = 0; si < subsetLen; si++)
            {
                int fi = subset[si];
                if (fi < features.Length)
                    sum += weights[weightOffset + si] * features[fi];
            }
        }

        return sum;
    }

    /// <summary>
    /// Simplified dot product for contiguous feature access (no subset indirection).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double DotProductSimdContiguous(double[] weights, int weightOffset, float[] features, int length)
    {
        double sum = 0.0;

        if (Vector.IsHardwareAccelerated && length >= Vector<double>.Count * 2)
        {
            int vecSize = Vector<double>.Count;
            int vecEnd = length - (length % vecSize);
            var vSum = Vector<double>.Zero;
            Span<double> featureBuf = stackalloc double[vecSize];

            for (int i = 0; i < vecEnd; i += vecSize)
            {
                var vW = new Vector<double>(weights, weightOffset + i);
                for (int vi = 0; vi < vecSize; vi++)
                    featureBuf[vi] = features[i + vi];
                var vF = new Vector<double>(featureBuf);
                vSum += vW * vF;
            }

            for (int vi = 0; vi < vecSize; vi++)
                sum += vSum[vi];

            for (int i = vecEnd; i < length; i++)
                sum += weights[weightOffset + i] * features[i];
        }
        else
        {
            for (int i = 0; i < length; i++)
                sum += weights[weightOffset + i] * features[i];
        }

        return sum;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Activation functions
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies the selected activation function to a pre-activation value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double Activate(double z, ElmActivation activation) => activation switch
    {
        ElmActivation.Sigmoid => MLFeatureHelper.Sigmoid(z),
        ElmActivation.Tanh    => Math.Tanh(z),
        ElmActivation.Relu    => Math.Max(0.0, z),
        _                     => MLFeatureHelper.Sigmoid(z),
    };

    // ═══════════════════════════════════════════════════════════════════════════
    //  Common math utilities
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double SampleGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// Produces a well-distributed seed from multiple integer inputs using a splitmix64-style
    /// finalizer.
    /// </summary>
    internal static int HashSeed(params int[] parts)
    {
        ulong h = 0;
        foreach (int p in parts)
        {
            h ^= unchecked((ulong)p);
            h ^= h >> 30;
            h *= 0xbf58476d1ce4e5b9UL;
            h ^= h >> 27;
            h *= 0x94d049bb133111ebUL;
            h ^= h >> 31;
        }
        return (int)(h & 0x7FFFFFFF);
    }

    internal static double CosineAnnealLr(double baseLr, int epoch, int maxEpochs, double minLr = 1e-6)
    {
        if (maxEpochs <= 1) return baseLr;
        double t = Math.Clamp((double)epoch / (maxEpochs - 1), 0.0, 1.0);
        return minLr + 0.5 * (baseLr - minLr) * (1.0 + Math.Cos(Math.PI * t));
    }

    internal static double ComputeSharpe(double[] returns, double annualisationFactor = 252.0)
    {
        if (returns.Length < 2) return 0;
        double sum = 0;
        for (int i = 0; i < returns.Length; i++) sum += returns[i];
        double mean = sum / returns.Length;
        double varSum = 0;
        for (int i = 0; i < returns.Length; i++)
        {
            double d = returns[i] - mean;
            varSum += d * d;
        }
        double std = Math.Sqrt(varSum / (returns.Length - 1));
        return std > 1e-10 ? mean / std * Math.Sqrt(annualisationFactor) : 0;
    }

    internal static double StdDev(IList<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sum = 0;
        for (int i = 0; i < values.Count; i++) { double d = values[i] - mean; sum += d * d; }
        return Math.Sqrt(sum / (values.Count - 1));
    }

    internal static double ComputeSharpeTrend(IReadOnlyList<double> sharpePerFold)
    {
        if (sharpePerFold.Count < 2) return 0;
        int n = sharpePerFold.Count;
        double sx = 0, sy = 0, sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++)
        {
            sx += i; sy += sharpePerFold[i];
            sxy += i * sharpePerFold[i]; sxx += i * i;
        }
        double den = n * sxx - sx * sx;
        return Math.Abs(den) > 1e-15 ? (n * sxy - sx * sy) / den : 0;
    }

    internal static void ShuffleArray(int[] arr, Random rng)
    {
        for (int i = arr.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
    }

    internal static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions, double annualisationFactor = 252.0)
    {
        if (predictions.Length == 0) return (0, 0);

        // Anchor the curve above zero so an immediate losing streak still registers as drawdown.
        double equity = 1.0, peak = 1.0, maxDD = 0.0;
        double[] returns = new double[predictions.Length];

        for (int i = 0; i < predictions.Length; i++)
        {
            double r = predictions[i].Predicted * predictions[i].Actual;
            returns[i] = r;
            equity += r;
            peak = Math.Max(peak, equity);
            double dd = peak - equity;
            maxDD = Math.Max(maxDD, peak > 0 ? dd / peak : 0.0);
        }

        double mean = returns.Average();
        double varSum = 0;
        for (int i = 0; i < returns.Length; i++)
        {
            double d = returns[i] - mean;
            varSum += d * d;
        }
        double std = returns.Length > 1 ? Math.Sqrt(varSum / (returns.Length - 1)) : 0;
        double sharpe = std > 1e-10 ? mean / std * Math.Sqrt(annualisationFactor) : 0;

        return (maxDD, sharpe);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Sherman-Morrison rank-1 online update
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Incrementally updates the ELM output weights using the Sherman-Morrison formula
    /// after observing a single new training sample, without recomputing the full
    /// (H^T H + λI)^{-1} inverse.
    /// <para>
    /// Given the current inverse P = (H^T H + λI)^{-1} and a new hidden activation
    /// vector h (1×H), the update is:
    /// <code>
    ///   P_new = P − (P h h^T P) / (1 + h^T P h)
    ///   w_new = w + P_new h (y − h^T w)
    /// </code>
    /// Cost: O(H²) per sample — one matrix-vector multiply + outer product.
    /// </para>
    /// </summary>
    /// <param name="inverseGram">
    /// The current (H×H) inverse Gram matrix P = (H^T H + λI)^{-1}.
    /// Updated in-place to P_new.
    /// </param>
    /// <param name="outputWeights">
    /// The current (H) output weight vector. Updated in-place.
    /// </param>
    /// <param name="outputBias">Current output bias scalar. Updated in-place.</param>
    /// <param name="hiddenActivation">
    /// The hidden-layer activation h for the new sample (length H).
    /// Computed by applying the frozen input weights + activation function to the raw features.
    /// </param>
    /// <param name="target">The direction label for the new sample (1.0 = Buy, 0.0 = Sell).</param>
    internal static void ShermanMorrisonUpdate(
        double[]  inverseGramFlat,
        int       gramDim,
        double[]  outputWeights,
        ref double outputBias,
        double[]  hiddenActivation,
        double    target,
        int       updateCount = 0)
    {
        int H = gramDim;

        // Ph = P × h  (H-vector)
        var Ph = new double[H];
        for (int i = 0; i < H; i++)
        {
            double sum = 0;
            for (int j = 0; j < H; j++)
                sum += inverseGramFlat[i * H + j] * hiddenActivation[j];
            Ph[i] = sum;
        }

        // denominator = 1 + h^T P h
        double denom = 1.0;
        for (int j = 0; j < H; j++)
            denom += hiddenActivation[j] * Ph[j];

        if (Math.Abs(denom) < 1e-15)
            return; // degenerate — skip this sample

        double invDenom = 1.0 / denom;

        // P_new = P − (Ph × Ph^T) / denom  (rank-1 downdate)
        for (int i = 0; i < H; i++)
            for (int j = 0; j < H; j++)
                inverseGramFlat[i * H + j] -= Ph[i] * Ph[j] * invDenom;

        // prediction error: e = y − (w^T h + b)
        double prediction = outputBias;
        for (int j = 0; j < H; j++)
            prediction += outputWeights[j] * hiddenActivation[j];
        double error = target - prediction;

        // P_new × h (recompute with updated P)
        var PnewH = new double[H];
        for (int i = 0; i < H; i++)
        {
            double sum = 0;
            for (int j = 0; j < H; j++)
                sum += inverseGramFlat[i * H + j] * hiddenActivation[j];
            PnewH[i] = sum;
        }

        // w_new = w + P_new h × error
        for (int i = 0; i < H; i++)
            outputWeights[i] += PnewH[i] * error;

        // bias update: adaptive 1/n learning rate (decays as more samples are seen)
        double biasLr = updateCount > 0 ? 1.0 / updateCount : 0.001;
        outputBias += biasLr * error;
    }
}
