using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;
using TorchSharp;
using static TorchSharp.torch;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class ElmModelTrainer
{
    private const int GpuMinSamples = 64;

    private static bool IsGpuAvailable()
    {
        try { return torch.cuda.is_available(); }
        catch { return false; }
    }

    /// <summary>
    /// GPU-accelerated density-ratio weight computation.
    /// </summary>
    private static double[]? TryComputeDensityRatioWeightsGpu(
        List<TrainingSample> trainSet,
        int                  F,
        int                  recentWindowDays,
        int                  barsPerDay,
        CancellationToken    ct = default)
    {
        if (!IsGpuAvailable() || trainSet.Count < GpuMinSamples)
            return null;

        try
        {
            int n = trainSet.Count;
            if (n < 50) return null;

            int resolvedBarsPerDay = barsPerDay > 0 ? barsPerDay : 24;
            int recentCount = Math.Max(10, Math.Min(n / 5, recentWindowDays * resolvedBarsPerDay));
            recentCount = Math.Min(recentCount, n - 10);
            int histCount = n - recentCount;

            var xArr = new float[n * F];
            var yArr = new float[n];
            for (int i = 0; i < n; i++)
            {
                Array.Copy(trainSet[i].Features, 0, xArr, i * F, F);
                yArr[i] = i >= histCount ? 1f : 0f;
            }

            using var wP = new TorchSharp.Modules.Parameter(zeros(F, 1, device: CUDA));
            using var bP = new TorchSharp.Modules.Parameter(zeros(1, device: CUDA));
            using var opt = optim.Adam([wP, bP], lr: 0.01, weight_decay: 0.01);

            using var xT = tensor(xArr, device: CUDA).reshape(n, F);
            using var yT = tensor(yArr, device: CUDA).reshape(n, 1);

            for (int epoch = 0; epoch < 40; epoch++)
            {
                ct.ThrowIfCancellationRequested();
                opt.zero_grad();
                using var logit = mm(xT, wP) + bP;
                using var prob = sigmoid(logit);
                using var loss = nn.functional.binary_cross_entropy(prob, yT);
                loss.backward();
                opt.step();
            }

            float[] scoreArr;
            using (no_grad())
            {
                using var logit = mm(xT, wP) + bP;
                using var prob = sigmoid(logit).squeeze(1);
                scoreArr = prob.cpu().data<float>().ToArray();
            }

            var weights = new double[n];
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double p = scoreArr[i];
                double ratio = Math.Clamp(p / Math.Max(1.0 - p, 1e-6), 0.01, 10.0);
                weights[i] = ratio;
                sum += ratio;
            }
            if (sum > 0) for (int i = 0; i < n; i++) weights[i] /= sum;
            return weights;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// GPU-accelerated adversarial validation AUC.
    /// </summary>
    private static double? TryComputeAdversarialAucGpu(
        List<TrainingSample> trainSet,
        List<TrainingSample> testSet,
        int                  F,
        CancellationToken    ct = default)
    {
        if (!IsGpuAvailable() || trainSet.Count + testSet.Count < GpuMinSamples)
            return null;

        try
        {
            int n1 = testSet.Count;
            int n0 = Math.Min(trainSet.Count, n1 * 5);
            int n = n0 + n1;
            if (n < 20) return 0.5;

            var trainSlice = trainSet.Count > n0 ? trainSet[^n0..] : trainSet;

            var xArr = new float[n * F];
            var yArr = new float[n];
            for (int i = 0; i < n0; i++)
            {
                Array.Copy(trainSlice[i].Features, 0, xArr, i * F, F);
                yArr[i] = 0f;
            }
            for (int i = 0; i < n1; i++)
            {
                Array.Copy(testSet[i].Features, 0, xArr, (n0 + i) * F, F);
                yArr[n0 + i] = 1f;
            }

            using var wP = new TorchSharp.Modules.Parameter(zeros(F, 1, device: CUDA));
            using var bP = new TorchSharp.Modules.Parameter(zeros(1, device: CUDA));
            using var opt = optim.Adam([wP, bP], lr: 0.005, weight_decay: 0.01);

            using var xT = tensor(xArr, device: CUDA).reshape(n, F);
            using var yT = tensor(yArr, device: CUDA).reshape(n, 1);

            for (int epoch = 0; epoch < 60; epoch++)
            {
                ct.ThrowIfCancellationRequested();
                opt.zero_grad();
                using var logit = mm(xT, wP) + bP;
                using var prob = sigmoid(logit);
                using var loss = nn.functional.binary_cross_entropy(prob, yT);
                loss.backward();
                opt.step();
            }

            float[] scoreArr;
            using (no_grad())
            {
                using var logit = mm(xT, wP) + bP;
                using var prob = sigmoid(logit).squeeze(1);
                scoreArr = prob.cpu().data<float>().ToArray();
            }

            var scores = new (float Score, int Label)[n];
            for (int i = 0; i < n; i++) scores[i] = (scoreArr[i], (int)yArr[i]);
            Array.Sort(scores, (a, b) => b.Score.CompareTo(a.Score));

            long tp = 0, aucNum = 0;
            foreach (var (_, lbl) in scores)
            {
                if (lbl == 1) tp++;
                else aucNum += tp;
            }
            return (n1 > 0 && n0 > 0) ? (double)aucNum / ((long)n1 * n0) : 0.5;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// GPU-accelerated Platt scaling fit.
    /// </summary>
    private static (double A, double B)? TryFitPlattScalingGpu(
        List<TrainingSample> calSet,
        Func<float[], double> rawProbFunc,
        CancellationToken ct = default)
    {
        if (!IsGpuAvailable() || calSet.Count < GpuMinSamples)
            return null;

        try
        {
            int n = calSet.Count;
            var logits = new float[n];
            var labels = new float[n];
            for (int i = 0; i < n; i++)
            {
                double raw = Math.Clamp(rawProbFunc(calSet[i].Features), 1e-7, 1.0 - 1e-7);
                logits[i] = (float)MLFeatureHelper.Logit(raw);
                labels[i] = calSet[i].Direction > 0 ? 1f : 0f;
            }

            using var wP = new TorchSharp.Modules.Parameter(ones(1, device: CUDA));
            using var bP = new TorchSharp.Modules.Parameter(zeros(1, device: CUDA));
            using var opt = optim.Adam([wP, bP], lr: 0.01);

            using var logitT = tensor(logits, device: CUDA);
            using var labelT = tensor(labels, device: CUDA);

            double prevLoss = double.MaxValue;
            int noImpro = 0;

            for (int epoch = 0; epoch < 200; epoch++)
            {
                ct.ThrowIfCancellationRequested();
                opt.zero_grad();
                using var z = logitT * wP.squeeze() + bP.squeeze();
                using var prob = sigmoid(z);
                using var loss = nn.functional.binary_cross_entropy(prob, labelT);
                loss.backward();
                opt.step();

                double curLoss = loss.item<float>();
                if (prevLoss - curLoss < 1e-6) { if (++noImpro >= 5) break; }
                else noImpro = 0;
                prevLoss = curLoss;
            }

            float a, b;
            using (no_grad())
            {
                a = wP.cpu().data<float>()[0];
                b = bP.cpu().data<float>()[0];
            }
            return (a, b);
        }
        catch
        {
            return null;
        }
    }
}
