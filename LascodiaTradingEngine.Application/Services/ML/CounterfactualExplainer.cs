using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Generates counterfactual explanations for ML predictions using constrained
/// projected gradient ascent in feature space.
/// </summary>
/// <remarks>
/// For a given prediction, the explainer seeks the minimal feature perturbation
/// Δ such that the ensemble's mean logit crosses the decision boundary, subject to
/// a per-feature budget of ±maxPerFeatureSigma standard deviations.
///
/// Algorithm (projected gradient ascent on P(flip | x)):
/// 1. Start from original feature vector x₀.
/// 2. Compute ensemble-averaged gradient of P(flip | x) w.r.t. x.
/// 3. Take step: x ← x + α × ∇P(flip | x).
/// 4. Project back onto the per-feature box constraint.
/// 5. Terminate when P(flip | x) ≥ 0.5 or max iterations reached.
/// 6. Return Δ = x_final − x₀ for features where |Δ/σ| > 0.01.
/// </remarks>
public class CounterfactualExplainer : ICounterfactualExplainer
{
    private readonly ILogger<CounterfactualExplainer> _logger;

    private const int   MaxIterations = 200;
    private const float StepSize      = 0.05f;
    private const float FlipThreshold = 0.50f;
    private const float MinDeltaSigma = 0.01f;

    public CounterfactualExplainer(ILogger<CounterfactualExplainer> logger)
    {
        _logger = logger;
    }

    public async Task<string?> ExplainAsync(
        float[]           features,
        TradeDirection    predictedDirection,
        ModelSnapshot     snapshot,
        string[]          featureNames,
        double            maxPerFeatureSigma = 2.0,
        CancellationToken ct                = default)
    {
        await Task.Yield();

        int F = features.Length;
        if (snapshot.Weights == null || snapshot.Weights.Length == 0)
            return null;

        // Flip target: if predicted Buy → we want Sell prob ≥ 0.5, i.e. Buy prob < 0.5
        bool wantBuyProb = predictedDirection == TradeDirection.Sell;

        // Per-feature standard deviations from training standardisation
        float[] featureStd = snapshot.Stds.Length >= F
            ? snapshot.Stds
            : Enumerable.Repeat(1f, F).ToArray();

        // Per-feature perturbation bounds
        float[] maxDelta = new float[F];
        for (int fi = 0; fi < F; fi++)
            maxDelta[fi] = (float)(featureStd[fi] * maxPerFeatureSigma);

        float[] x = (float[])features.Clone();

        for (int iter = 0; iter < MaxIterations && !ct.IsCancellationRequested; iter++)
        {
            float prob = ScoreEnsemble(x, snapshot, wantBuyProb);
            if (prob >= FlipThreshold) break;

            float[] grad = ComputeEnsembleGradient(x, snapshot, wantBuyProb, F);

            for (int fi = 0; fi < F; fi++)
                x[fi] += StepSize * grad[fi];

            // Project onto box constraint
            for (int fi = 0; fi < F; fi++)
            {
                float delta = x[fi] - features[fi];
                delta  = Math.Clamp(delta, -maxDelta[fi], maxDelta[fi]);
                x[fi]  = features[fi] + delta;
            }
        }

        float finalProb = ScoreEnsemble(x, snapshot, wantBuyProb);
        if (finalProb < FlipThreshold)
        {
            _logger.LogDebug("Counterfactual: no flip within budget (final prob={P:F3})", finalProb);
            return null;
        }

        var deltaMap = new Dictionary<string, string>();
        for (int fi = 0; fi < F && fi < featureNames.Length; fi++)
        {
            float d = x[fi] - features[fi];
            if (featureStd[fi] > 0 && Math.Abs(d / featureStd[fi]) > MinDeltaSigma)
            {
                string sign = d > 0 ? "+" : "";
                deltaMap[featureNames[fi]] = $"{sign}{d:F3}";
            }
        }

        return deltaMap.Count > 0 ? JsonSerializer.Serialize(deltaMap) : null;
    }

    // ── Ensemble scoring ──────────────────────────────────────────────────────

    private static float ScoreEnsemble(float[] x, ModelSnapshot snap, bool wantBuyProb)
    {
        int K = snap.Weights.Length;
        if (K == 0) return 0f;

        float sumProb = 0f;
        for (int k = 0; k < K; k++)
        {
            float logit = (float)(k < snap.Biases.Length ? snap.Biases[k] : 0);
            var   w     = snap.Weights[k];
            int   wLen  = Math.Min(x.Length, w.Length);
            for (int fi = 0; fi < wLen; fi++)
                logit += (float)w[fi] * x[fi];
            float p = Sigmoid(logit);
            sumProb += wantBuyProb ? p : 1f - p;
        }
        return sumProb / K;
    }

    private static float[] ComputeEnsembleGradient(float[] x, ModelSnapshot snap, bool wantBuyProb, int F)
    {
        int K = snap.Weights.Length;
        var grad = new float[F];

        for (int k = 0; k < K; k++)
        {
            float logit = (float)(k < snap.Biases.Length ? snap.Biases[k] : 0);
            var   w     = snap.Weights[k];
            int   wLen  = Math.Min(x.Length, w.Length);
            for (int fi = 0; fi < wLen; fi++)
                logit += (float)w[fi] * x[fi];

            float p     = Sigmoid(logit);
            float dPdL  = p * (1 - p);
            float sign  = wantBuyProb ? 1f : -1f;

            for (int fi = 0; fi < F && fi < wLen; fi++)
                grad[fi] += sign * dPdL * (float)w[fi];
        }

        // Normalise by K
        for (int fi = 0; fi < F; fi++) grad[fi] /= K;
        return grad;
    }

    private static float Sigmoid(float z)
        => 1f / (1f + (float)Math.Exp(-Math.Clamp(z, -20f, 20f)));
}
