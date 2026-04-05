using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class FtTransformerModelTrainer
{
    // ── LayerNorm ─────────────────────────────────────────────────────────────

    private static void LayerNormForward(double[] x, double[] gamma, double[] beta, double[] y, int D)
    {
        double mean = 0;
        for (int d = 0; d < D; d++) mean += x[d];
        mean /= D;

        double variance = 0;
        for (int d = 0; d < D; d++) { double diff = x[d] - mean; variance += diff * diff; }
        double invStd = 1.0 / Math.Sqrt(variance / D + 1e-8);

        for (int d = 0; d < D; d++)
            y[d] = gamma[d] * (x[d] - mean) * invStd + beta[d];
    }

    private static void LayerNormForwardCached(
        double[] x, double[] gamma, double[] beta, double[] y,
        int D, ref double outMean, ref double outInvStd, double[] outNorm)
    {
        double mean = 0;
        for (int d = 0; d < D; d++) mean += x[d];
        mean /= D;

        double variance = 0;
        for (int d = 0; d < D; d++) { double diff = x[d] - mean; variance += diff * diff; }
        double invStd = 1.0 / Math.Sqrt(variance / D + 1e-8);

        outMean   = mean;
        outInvStd = invStd;

        for (int d = 0; d < D; d++)
        {
            outNorm[d] = (x[d] - mean) * invStd;
            y[d] = gamma[d] * outNorm[d] + beta[d];
        }
    }

    private static void LayerNormBackward(
        double[] dOut, double[] norm, double[] gamma, double invStd,
        int D, double[] dx, double[] dGamma, double[] dBeta)
    {
        // dGamma, dBeta accumulate across positions
        for (int d = 0; d < D; d++)
        {
            dGamma[d] += dOut[d] * norm[d];
            dBeta[d]  += dOut[d];
        }

        // dx = invStd * (gamma * dOut - mean(gamma * dOut) - norm * mean(gamma * dOut * norm))
        double s1 = 0, s2 = 0;
        for (int d = 0; d < D; d++)
        {
            double gd = gamma[d] * dOut[d];
            s1 += gd;
            s2 += gd * norm[d];
        }
        s1 /= D;
        s2 /= D;

        for (int d = 0; d < D; d++)
            dx[d] = invStd * (gamma[d] * dOut[d] - s1 - norm[d] * s2);
    }

    // ── Activation functions ──────────────────────────────────────────────────

    private static double GELU(double x)
    {
        // Approximate GELU: x * 0.5 * (1 + tanh(sqrt(2/pi) * (x + 0.044715 * x^3)))
        const double sqrt2OverPi = 0.7978845608028654;
        double inner = sqrt2OverPi * (x + 0.044715 * x * x * x);
        return 0.5 * x * (1.0 + Math.Tanh(inner));
    }

    private static double GELUGrad(double x)
    {
        const double sqrt2OverPi = 0.7978845608028654;
        double x3 = x * x * x;
        double inner = sqrt2OverPi * (x + 0.044715 * x3);
        double tanh = Math.Tanh(inner);
        double sech2 = 1.0 - tanh * tanh;
        double dInner = sqrt2OverPi * (1.0 + 3.0 * 0.044715 * x * x);
        return 0.5 * (1.0 + tanh) + 0.5 * x * sech2 * dInner;
    }

    // ── Matrix multiply helper (Improvement 9: B transposition for cache locality) ──

    [ThreadStatic] private static double[][]? _matMulBt;

    private static void MatMul(double[][] A, double[][] B, double[][] C, int M, int K, int N)
    {
        // Transpose B for cache-friendly access
        if (_matMulBt is null || _matMulBt.Length < N)
            _matMulBt = new double[N][];
        for (int j = 0; j < N; j++)
        {
            if (_matMulBt[j] is null || _matMulBt[j].Length < K)
                _matMulBt[j] = new double[K];
            for (int k = 0; k < K; k++)
                _matMulBt[j][k] = B[k][j];
        }

        for (int i = 0; i < M; i++)
            for (int j = 0; j < N; j++)
            {
                double s = 0;
                var ai = A[i];
                var btj = _matMulBt[j];
                for (int k = 0; k < K; k++)
                    s += ai[k] * btj[k];
                C[i][j] = s;
            }
    }

    // ── Weight sanitisation ──────────────────────────────────────────────────

    private static int SanitiseWeights(TransformerModel model)
    {
        int count = 0;
        count += SanitiseMatrix(model.We, model.F);
        count += SanitiseMatrix(model.Be, model.F);
        count += SanitiseArray(model.ClsToken);
        for (int l = 0; l < model.NumLayers; l++)
        {
            var L = model.Layers[l];
            count += SanitiseMatrix(L.Wq, model.EmbedDim);
            count += SanitiseMatrix(L.Wk, model.EmbedDim);
            count += SanitiseMatrix(L.Wv, model.EmbedDim);
            count += SanitiseMatrix(L.Wo, model.EmbedDim);
            count += SanitiseArray(L.Gamma1);
            count += SanitiseArray(L.Beta1);
            count += SanitiseMatrix(L.Wff1, model.EmbedDim);
            count += SanitiseArray(L.Bff1);
            count += SanitiseMatrix(L.Wff2, model.FfnDim);
            count += SanitiseArray(L.Bff2);
            count += SanitiseArray(L.Gamma2);
            count += SanitiseArray(L.Beta2);
        }
        count += SanitiseArray(model.GammaFinal);
        count += SanitiseArray(model.BetaFinal);
        count += SanitiseArray(model.WOut);
        if (!double.IsFinite(model.BOut)) { model.BOut = 0.0; count++; }
        return count;
    }

    private static int SanitiseMatrix(double[][] m, int rows)
    {
        int count = 0;
        for (int r = 0; r < rows; r++)
            if (HasNonFiniteArray(m[r])) { Array.Clear(m[r]); count++; }
        return count;
    }

    private static int SanitiseArray(double[] a)
    {
        if (HasNonFiniteArray(a)) { Array.Clear(a); return 1; }
        return 0;
    }

    // ── Model cloning (Improvement 8: zero-alloc CopyModel) ─────────────────

    private static TransformerModel CloneModel(TransformerModel src)
    {
        var dst = new TransformerModel(src.F, src.EmbedDim, src.NumHeads, src.FfnDim, src.NumLayers);
        CopyModel(src, dst);
        return dst;
    }

    private static void CopyModel(TransformerModel src, TransformerModel dst)
    {
        int D  = src.EmbedDim;
        int Ff = src.FfnDim;

        for (int f = 0; f < src.F; f++)
        {
            if (dst.We[f] is null || dst.We[f].Length != D) dst.We[f] = new double[D];
            Array.Copy(src.We[f], dst.We[f], D);
            if (dst.Be[f] is null || dst.Be[f].Length != D) dst.Be[f] = new double[D];
            Array.Copy(src.Be[f], dst.Be[f], D);
        }

        Array.Copy(src.ClsToken, dst.ClsToken, D);

        for (int l = 0; l < src.NumLayers; l++)
        {
            var sL = src.Layers[l];
            var dL = dst.Layers[l];

            for (int d = 0; d < D; d++)
            {
                if (dL.Wq[d] is null || dL.Wq[d].Length != D) dL.Wq[d] = new double[D];
                Array.Copy(sL.Wq[d], dL.Wq[d], D);

                if (dL.Wk[d] is null || dL.Wk[d].Length != D) dL.Wk[d] = new double[D];
                Array.Copy(sL.Wk[d], dL.Wk[d], D);

                if (dL.Wv[d] is null || dL.Wv[d].Length != D) dL.Wv[d] = new double[D];
                Array.Copy(sL.Wv[d], dL.Wv[d], D);

                if (dL.Wo[d] is null || dL.Wo[d].Length != D) dL.Wo[d] = new double[D];
                Array.Copy(sL.Wo[d], dL.Wo[d], D);
            }

            Array.Copy(sL.Gamma1, dL.Gamma1, D);
            Array.Copy(sL.Beta1, dL.Beta1, D);

            for (int d = 0; d < D; d++)
            {
                if (dL.Wff1[d] is null || dL.Wff1[d].Length != Ff) dL.Wff1[d] = new double[Ff];
                Array.Copy(sL.Wff1[d], dL.Wff1[d], Ff);
            }

            Array.Copy(sL.Bff1, dL.Bff1, Ff);

            for (int d = 0; d < Ff; d++)
            {
                if (dL.Wff2[d] is null || dL.Wff2[d].Length != D) dL.Wff2[d] = new double[D];
                Array.Copy(sL.Wff2[d], dL.Wff2[d], D);
            }

            Array.Copy(sL.Bff2, dL.Bff2, D);
            Array.Copy(sL.Gamma2, dL.Gamma2, D);
            Array.Copy(sL.Beta2, dL.Beta2, D);
        }

        Array.Copy(src.GammaFinal, dst.GammaFinal, D);
        Array.Copy(src.BetaFinal, dst.BetaFinal, D);
        Array.Copy(src.WOut, dst.WOut, D);
        dst.BOut = src.BOut;
    }

    // ── Weight clipping ──────────────────────────────────────────────────────

    private static void ClipWeights(TransformerModel model, double maxMag)
    {
        ClipMatrix(model.We, model.F, maxMag);
        ClipMatrix(model.Be, model.F, maxMag);
        ClipArray(model.ClsToken, maxMag);
        for (int l = 0; l < model.NumLayers; l++)
        {
            var L = model.Layers[l];
            ClipMatrix(L.Wq, model.EmbedDim, maxMag);
            ClipMatrix(L.Wk, model.EmbedDim, maxMag);
            ClipMatrix(L.Wv, model.EmbedDim, maxMag);
            ClipMatrix(L.Wo, model.EmbedDim, maxMag);
            ClipArray(L.Gamma1, maxMag); ClipArray(L.Beta1, maxMag);
            ClipMatrix(L.Wff1, model.EmbedDim, maxMag);
            ClipArray(L.Bff1, maxMag);
            ClipMatrix(L.Wff2, model.FfnDim, maxMag);
            ClipArray(L.Bff2, maxMag);
            ClipArray(L.Gamma2, maxMag); ClipArray(L.Beta2, maxMag);
        }
        ClipArray(model.GammaFinal, maxMag);
        ClipArray(model.BetaFinal, maxMag);
        ClipArray(model.WOut, maxMag);
        model.BOut = Math.Clamp(model.BOut, -maxMag, maxMag);
    }

    private static void ClipMatrix(double[][] m, int rows, double maxMag)
    {
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < m[r].Length; c++)
                m[r][c] = Math.Clamp(m[r][c], -maxMag, maxMag);
    }

    private static void ClipArray(double[] a, double maxMag)
    {
        for (int i = 0; i < a.Length; i++)
            a[i] = Math.Clamp(a[i], -maxMag, maxMag);
    }

    // ── Non-finite checks ────────────────────────────────────────────────────

    private static bool HasNonFinite(TransformerModel model)
    {
        if (!double.IsFinite(model.BOut)) return true;
        if (HasNonFiniteArray(model.WOut)) return true;
        if (HasNonFiniteArray(model.ClsToken)) return true;
        if (HasNonFiniteArray(model.GammaFinal)) return true;
        if (HasNonFiniteArray(model.BetaFinal)) return true;
        for (int f = 0; f < model.F; f++)
            if (HasNonFiniteArray(model.We[f]) || HasNonFiniteArray(model.Be[f])) return true;
        for (int l = 0; l < model.NumLayers; l++)
        {
            var L = model.Layers[l];
            for (int d = 0; d < model.EmbedDim; d++)
                if (HasNonFiniteArray(L.Wq[d]) || HasNonFiniteArray(L.Wk[d]) ||
                    HasNonFiniteArray(L.Wv[d]) || HasNonFiniteArray(L.Wo[d])) return true;
            if (HasNonFiniteArray(L.Gamma1) || HasNonFiniteArray(L.Beta1)) return true;
            if (HasNonFiniteArray(L.Gamma2) || HasNonFiniteArray(L.Beta2)) return true;
            for (int d = 0; d < model.EmbedDim; d++)
                if (HasNonFiniteArray(L.Wff1[d])) return true;
            if (HasNonFiniteArray(L.Bff1)) return true;
            for (int d = 0; d < model.FfnDim; d++)
                if (HasNonFiniteArray(L.Wff2[d])) return true;
            if (HasNonFiniteArray(L.Bff2)) return true;
        }
        return false;
    }

    private static bool HasNonFiniteArray(double[] arr)
    {
        for (int i = 0; i < arr.Length; i++) if (!double.IsFinite(arr[i])) return true;
        return false;
    }

    // ── Statistics helpers ────────────────────────────────────────────────────

    private static double ComputeSharpe(double[] returns, int count, double annualisationFactor = 252.0)
    {
        if (count < 2) return 0.0;
        double sum = 0;
        for (int i = 0; i < count; i++) sum += returns[i];
        double mean = sum / count;
        double varSum = 0;
        for (int i = 0; i < count; i++) { double d = returns[i] - mean; varSum += d * d; }
        double std = Math.Sqrt(varSum / (count - 1));
        return std > 1e-10 ? mean / std * Math.Sqrt(annualisationFactor) : 0.0;
    }

    private static double StdDev(IEnumerable<double> values, double mean)
    {
        double sum = 0; int count = 0;
        foreach (double v in values) { sum += (v - mean) * (v - mean); count++; }
        return count > 1 ? Math.Sqrt(sum / (count - 1)) : 0.0;
    }

    private static double ComputeSharpeTrend(List<double> sharpePerFold)
    {
        if (sharpePerFold.Count < 3) return 0.0;
        int n = sharpePerFold.Count;
        double xMean = (n - 1) / 2.0;
        double yMean = 0; foreach (var s in sharpePerFold) yMean += s; yMean /= n;
        double num = 0, den = 0;
        for (int i = 0; i < n; i++) { double dx = i - xMean; num += dx * (sharpePerFold[i] - yMean); den += dx * dx; }
        return den > 1e-15 ? num / den : 0.0;
    }

    private static double SampleGaussian(Random rng, double std)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return std * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // ── Adam optimiser updates ───────────────────────────────────────────────

    /// <summary>
    /// AdamW: decoupled weight decay applied directly to weights, not via gradients.
    /// Weight decay is applied as w -= lr * wd * w independently from the Adam moment updates.
    /// </summary>
    private static void ApplyAdamUpdates(
        TransformerModel model, TransformerGrad grad, AdamState adam, double alphAt,
        int F, int D, int Ff, double wd)
    {
        AdamWUpdate2D(model.We, grad.dWe, adam.mWe, adam.vWe, alphAt, F, D, wd);
        AdamWUpdate2D(model.Be, grad.dBe, adam.mBe, adam.vBe, alphAt, F, D, wd);
        AdamWUpdate1D(model.ClsToken, grad.dClsToken, adam.mClsToken, adam.vClsToken, alphAt, D, wd);

        for (int l = 0; l < model.NumLayers; l++)
        {
            var L  = model.Layers[l];
            var lg = grad.LayerGrads[l];
            var la = adam.LayerStates[l];

            AdamWUpdate2D(L.Wq, lg.dWq, la.mWq, la.vWq, alphAt, D, D, wd);
            AdamWUpdate2D(L.Wk, lg.dWk, la.mWk, la.vWk, alphAt, D, D, wd);
            AdamWUpdate2D(L.Wv, lg.dWv, la.mWv, la.vWv, alphAt, D, D, wd);
            AdamWUpdate2D(L.Wo, lg.dWo, la.mWo, la.vWo, alphAt, D, D, wd);
            // No weight decay on LayerNorm parameters (gamma, beta) — standard practice
            AdamWUpdate1D(L.Gamma1, lg.dGamma1, la.mGamma1, la.vGamma1, alphAt, D, 0.0);
            AdamWUpdate1D(L.Beta1,  lg.dBeta1,  la.mBeta1,  la.vBeta1,  alphAt, D, 0.0);
            AdamWUpdate2D(L.Wff1, lg.dWff1, la.mWff1, la.vWff1, alphAt, D, Ff, wd);
            AdamWUpdate1D(L.Bff1, lg.dBff1, la.mBff1, la.vBff1, alphAt, Ff, 0.0);
            AdamWUpdate2D(L.Wff2, lg.dWff2, la.mWff2, la.vWff2, alphAt, Ff, D, wd);
            AdamWUpdate1D(L.Bff2, lg.dBff2, la.mBff2, la.vBff2, alphAt, D, 0.0);
            // No weight decay on LayerNorm parameters
            AdamWUpdate1D(L.Gamma2, lg.dGamma2, la.mGamma2, la.vGamma2, alphAt, D, 0.0);
            AdamWUpdate1D(L.Beta2,  lg.dBeta2,  la.mBeta2,  la.vBeta2,  alphAt, D, 0.0);
        }

        // No weight decay on final LayerNorm parameters
        AdamWUpdate1D(model.GammaFinal, grad.dGammaFinal, adam.mGammaFinal, adam.vGammaFinal, alphAt, D, 0.0);
        AdamWUpdate1D(model.BetaFinal,  grad.dBetaFinal,  adam.mBetaFinal,  adam.vBetaFinal,  alphAt, D, 0.0);
        AdamWUpdate1D(model.WOut, grad.dWOut, adam.mWOut, adam.vWOut, alphAt, D, wd);

        adam.mBOut = AdamBeta1 * adam.mBOut + (1.0 - AdamBeta1) * grad.dBOut;
        adam.vBOut = AdamBeta2 * adam.vBOut + (1.0 - AdamBeta2) * grad.dBOut * grad.dBOut;
        model.BOut -= alphAt * adam.mBOut / (Math.Sqrt(adam.vBOut) + AdamEpsilon);
    }

    private static void AdamWUpdate2D(double[][] w, double[][] g, double[][] m, double[][] v, double lr, int r, int c, double wd)
    {
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
            {
                m[i][j] = AdamBeta1 * m[i][j] + (1.0 - AdamBeta1) * g[i][j];
                v[i][j] = AdamBeta2 * v[i][j] + (1.0 - AdamBeta2) * g[i][j] * g[i][j];
                w[i][j] -= lr * (m[i][j] / (Math.Sqrt(v[i][j]) + AdamEpsilon) + wd * w[i][j]);
            }
    }

    private static void AdamWUpdate1D(double[] w, double[] g, double[] m, double[] v, double lr, int n, double wd)
    {
        for (int i = 0; i < n; i++)
        {
            m[i] = AdamBeta1 * m[i] + (1.0 - AdamBeta1) * g[i];
            v[i] = AdamBeta2 * v[i] + (1.0 - AdamBeta2) * g[i] * g[i];
            w[i] -= lr * (m[i] / (Math.Sqrt(v[i]) + AdamEpsilon) + wd * w[i]);
        }
    }
}
