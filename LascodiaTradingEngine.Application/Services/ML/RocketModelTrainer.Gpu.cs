using LascodiaTradingEngine.Application.MLModels.Shared;
using TorchSharp;
using static TorchSharp.torch;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class RocketModelTrainer
{
    private const int GpuMinSamples = 64;

    private static bool IsGpuAvailable()
    {
        try { return torch.cuda.is_available(); }
        catch { return false; }
    }

    private static double? TryComputeAdversarialAucGpu(
        List<TrainingSample> trainSet, List<TrainingSample> testSet, int F,
        CancellationToken ct = default)
    {
        if (!IsGpuAvailable() || trainSet.Count + testSet.Count < GpuMinSamples) return null;
        try
        {
            int n1 = testSet.Count; int n0 = Math.Min(trainSet.Count, n1 * 5); int n = n0 + n1;
            if (n < 20) return 0.5;
            var trainSlice = trainSet.Count > n0 ? trainSet[^n0..] : trainSet;

            var xArr = new float[n * F]; var yArr = new float[n];
            for (int i = 0; i < n0; i++) { Array.Copy(trainSlice[i].Features, 0, xArr, i * F, F); }
            for (int i = 0; i < n1; i++) { Array.Copy(testSet[i].Features, 0, xArr, (n0 + i) * F, F); yArr[n0 + i] = 1f; }

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
            foreach (var (_, lbl) in scores) { if (lbl == 1) tp++; else aucNum += tp; }
            return (n1 > 0 && n0 > 0) ? (double)aucNum / ((long)n1 * n0) : 0.5;
        }
        catch { return null; }
    }

    private static double ComputeAdversarialAuc(List<TrainingSample> trainSet, List<TrainingSample> testSet, int F)
    {
        int n1 = testSet.Count; int n0 = Math.Min(trainSet.Count, n1 * 5); int n = n0 + n1;
        if (n < 20) return 0.5;
        var trainSlice = trainSet.Count > n0 ? trainSet[^n0..] : trainSet;
        var w = new double[F]; double b = 0;
        for (int epoch = 0; epoch < 60; epoch++)
        {
            double dB = 0; var dW = new double[F];
            for (int i = 0; i < n; i++)
            {
                float[] features = i < n0 ? trainSlice[i].Features : testSet[i - n0].Features;
                double label = i < n0 ? 0.0 : 1.0;
                double z = b; for (int j = 0; j < F && j < features.Length; j++) z += w[j] * features[j];
                double p = 1.0 / (1.0 + Math.Exp(-z)); double err = p - label;
                dB += err; for (int j = 0; j < F && j < features.Length; j++) dW[j] += err * features[j];
            }
            b -= 0.005 * dB / n; for (int j = 0; j < F; j++) w[j] -= 0.005 * (dW[j] / n + 0.01 * w[j]);
        }
        var scores = new (double Score, int Label)[n];
        for (int i = 0; i < n; i++)
        {
            float[] features = i < n0 ? trainSlice[i].Features : testSet[i - n0].Features;
            double z = b; for (int j = 0; j < F && j < features.Length; j++) z += w[j] * features[j];
            scores[i] = (1.0 / (1.0 + Math.Exp(-z)), i < n0 ? 0 : 1);
        }
        Array.Sort(scores, (a, c) => c.Score.CompareTo(a.Score));
        long tp = 0, aucNum = 0;
        foreach (var (_, lbl) in scores) { if (lbl == 1) tp++; else aucNum += tp; }
        return (n1 > 0 && n0 > 0) ? (double)aucNum / ((long)n1 * n0) : 0.5;
    }

    private static double[]? TryComputeDensityRatioWeightsGpu(
        List<TrainingSample> trainSet, int F, int recentWindowDays, int barsPerDay,
        CancellationToken ct = default)
    {
        if (!IsGpuAvailable() || trainSet.Count < GpuMinSamples) return null;
        try
        {
            int n = trainSet.Count;
            if (n < 50) return null;
            int resolvedBpd = barsPerDay > 0 ? barsPerDay : 24;
            int recentCount = Math.Max(10, Math.Min(n / 5, recentWindowDays * resolvedBpd));
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
        catch { return null; }
    }
}
