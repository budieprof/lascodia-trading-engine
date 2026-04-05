using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TcnModelTrainer
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Item 37 – Progressive Layer Unfreezing
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determines which TCN blocks should be frozen (not updated) at a given epoch during
    /// progressive unfreezing. Lower blocks (learning general temporal features) stay frozen
    /// longer to prevent catastrophic forgetting.
    /// </summary>
    /// <param name="numBlocks">Total number of TCN blocks.</param>
    /// <param name="epoch">Current training epoch.</param>
    /// <param name="unfreezeEpochsPerBlock">How many epochs to wait before unfreezing the next block.</param>
    /// <returns>Boolean array where true = block is frozen (skip gradient updates).</returns>
    internal static bool[] ComputeFrozenBlocks(int numBlocks, int epoch, int unfreezeEpochsPerBlock)
    {
        if (unfreezeEpochsPerBlock <= 0) return new bool[numBlocks]; // all unfrozen

        var frozen = new bool[numBlocks];
        // Unfreeze from top (deepest) to bottom (shallowest)
        // At epoch 0, only the top block is unfrozen
        // At epoch unfreezeEpochsPerBlock, top 2 blocks are unfrozen, etc.
        int unfrozenFromTop = Math.Min(numBlocks, 1 + epoch / unfreezeEpochsPerBlock);
        for (int b = 0; b < numBlocks - unfrozenFromTop; b++)
            frozen[b] = true;

        return frozen;
    }

    /// <summary>
    /// Zeros out gradient accumulators for frozen blocks to prevent weight updates.
    /// Call after backward pass, before Adam update.
    /// </summary>
    internal static void ZeroFrozenGradients(
        bool[] frozenBlocks, double[][] batchGradConvW, double[][] batchGradConvB,
        double[][] batchGradResW, double[]?[] resW,
        double[][]? batchGradLnG, double[][]? batchGradLnB, bool useLayerNorm)
    {
        for (int b = 0; b < frozenBlocks.Length; b++)
        {
            if (!frozenBlocks[b]) continue;
            Array.Clear(batchGradConvW[b]);
            Array.Clear(batchGradConvB[b]);
            if (resW[b] != null && batchGradResW[b] != null)
                Array.Clear(batchGradResW[b]);
            if (useLayerNorm)
            {
                if (batchGradLnG != null) Array.Clear(batchGradLnG[b]);
                if (batchGradLnB != null) Array.Clear(batchGradLnB[b]);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Item 38 – Elastic Weight Consolidation (EWC)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the approximate Fisher Information diagonal for each parameter.
    /// F_i approximately equals (1/N) times the sum of (dL/d_theta_i)^2 -- the average squared gradient
    /// over training data. This is computed on the PARENT model's weights to identify which
    /// parameters were important for the previous task.
    /// </summary>
    internal static double[] ComputeFisherDiagonal(
        List<TrainingSample> samples, TcnWeights tcn, int filters, bool useAttentionPool,
        int maxSamples = 200)
    {
        // Flatten all trainable parameters into a single vector to compute Fisher
        int totalParams = CountTotalParams(tcn, filters);
        var fisher = new double[totalParams];

        int n = Math.Min(samples.Count, maxSamples);
        for (int si = 0; si < n; si++)
        {
            double p = Math.Clamp(TcnProb(samples[si], tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
            double y = samples[si].Direction > 0 ? 1.0 : 0.0;
            // For binary classification, Fisher = p(1-p) for each parameter's contribution
            // Simplified: use the squared gradient of the log-likelihood
            double gradScale = (p - y) * (p - y);

            // Distribute across all parameters proportionally
            // This is a simplified diagonal Fisher -- in practice we'd need per-param gradients
            // but that requires a full backward pass per sample. Use the approximation:
            // F_i approximately equals grad_scale * w_i^2 (parameter magnitude as proxy for sensitivity)
            int idx = 0;
            for (int b = 0; b < tcn.ConvW.Length; b++)
            {
                for (int wi = 0; wi < tcn.ConvW[b].Length; wi++)
                    fisher[idx++] += gradScale * tcn.ConvW[b][wi] * tcn.ConvW[b][wi];
                for (int o = 0; o < tcn.ConvB[b].Length; o++)
                    fisher[idx++] += gradScale * tcn.ConvB[b][o] * tcn.ConvB[b][o];
            }
            for (int wi = 0; wi < tcn.HeadW.Length; wi++)
                fisher[idx++] += gradScale * tcn.HeadW[wi] * tcn.HeadW[wi];
            for (int wi = 0; wi < tcn.HeadB.Length; wi++)
                fisher[idx++] += gradScale * tcn.HeadB[wi] * tcn.HeadB[wi];
            for (int wi = 0; wi < tcn.MagHeadW.Length; wi++)
                fisher[idx++] += gradScale * tcn.MagHeadW[wi] * tcn.MagHeadW[wi];
        }

        // Normalise
        if (n > 0)
            for (int i = 0; i < fisher.Length; i++) fisher[i] /= n;

        return fisher;
    }

    /// <summary>
    /// Computes the EWC penalty gradient: 2 * lambda * F_i * (theta_i - theta*_i) for each parameter.
    /// This penalty is added to the standard gradient during training to prevent
    /// the new model from deviating too far from the parent on important parameters.
    /// </summary>
    internal static void AddEwcPenaltyGradient(
        double[] ewcFisher, double[] parentParams, double[] currentParams,
        double[] gradientAccum, double ewcLambda, int length)
    {
        for (int i = 0; i < length; i++)
            gradientAccum[i] += 2.0 * ewcLambda * ewcFisher[i] * (currentParams[i] - parentParams[i]);
    }

    /// <summary>Flattens all TCN parameters into a single double array for EWC comparison.</summary>
    internal static double[] FlattenParams(TcnWeights tcn, int filters)
    {
        int total = CountTotalParams(tcn, filters);
        var flat = new double[total];
        int idx = 0;
        for (int b = 0; b < tcn.ConvW.Length; b++)
        {
            Array.Copy(tcn.ConvW[b], 0, flat, idx, tcn.ConvW[b].Length); idx += tcn.ConvW[b].Length;
            Array.Copy(tcn.ConvB[b], 0, flat, idx, tcn.ConvB[b].Length); idx += tcn.ConvB[b].Length;
        }
        Array.Copy(tcn.HeadW, 0, flat, idx, tcn.HeadW.Length); idx += tcn.HeadW.Length;
        Array.Copy(tcn.HeadB, 0, flat, idx, tcn.HeadB.Length); idx += tcn.HeadB.Length;
        Array.Copy(tcn.MagHeadW, 0, flat, idx, tcn.MagHeadW.Length); idx += tcn.MagHeadW.Length;
        return flat;
    }

    private static int CountTotalParams(TcnWeights tcn, int filters)
    {
        int total = 0;
        for (int b = 0; b < tcn.ConvW.Length; b++)
            total += tcn.ConvW[b].Length + tcn.ConvB[b].Length;
        total += tcn.HeadW.Length + tcn.HeadB.Length + tcn.MagHeadW.Length;
        return total;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Item 39 – Knowledge Distillation from Parent
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the knowledge distillation loss: KL(teacher_probs || student_probs) scaled by T^2.
    /// The parent model's probability outputs serve as soft targets.
    /// Returns the additional loss and gradient contribution.
    /// </summary>
    internal static (double Loss, double Gradient) ComputeDistillationLoss(
        double studentProb, double teacherProb, double temperature, double distillWeight)
    {
        if (temperature <= 0 || distillWeight <= 0) return (0, 0);

        // Soften both probabilities with temperature
        double sLogit = LogitFromProb(studentProb) / temperature;
        double tLogit = LogitFromProb(teacherProb) / temperature;

        double sP = 1.0 / (1.0 + Math.Exp(-sLogit));
        double tP = 1.0 / (1.0 + Math.Exp(-tLogit));

        sP = Math.Clamp(sP, 1e-7, 1 - 1e-7);
        tP = Math.Clamp(tP, 1e-7, 1 - 1e-7);

        // KL(teacher || student) = sum of t * log(t/s) -- standard KL divergence
        double kl = tP * Math.Log(tP / sP) + (1 - tP) * Math.Log((1 - tP) / (1 - sP));
        double loss = distillWeight * temperature * temperature * kl;

        // Gradient w.r.t. student logit: distillWeight * T * (sP - tP)
        double grad = distillWeight * temperature * (sP - tP);

        return (loss, grad);
    }

    private static double LogitFromProb(double p)
    {
        p = Math.Clamp(p, 1e-7, 1 - 1e-7);
        return Math.Log(p / (1 - p));
    }

    /// <summary>
    /// Pre-computes parent model probabilities for the training set (for distillation).
    /// </summary>
    internal static double[] PrecomputeTeacherProbs(
        List<TrainingSample> samples, TcnWeights parentTcn, int filters, bool useAttentionPool)
    {
        return PrecomputeRawProbs(samples, parentTcn, filters, useAttentionPool);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Item 40 – Channel Importance Transfer
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Biases attention query weights toward channels that the parent model found important.
    /// Scales the query weight rows corresponding to important channels by (1 + importance).
    /// </summary>
    internal static void TransferChannelImportance(
        double[] attnQueryW, double[] parentImportanceScores, int filters)
    {
        if (parentImportanceScores.Length == 0 || attnQueryW.Length == 0) return;

        // Normalise importance scores to [0, 1]
        double maxImp = 0;
        for (int i = 0; i < parentImportanceScores.Length; i++)
            if (parentImportanceScores[i] > maxImp) maxImp = parentImportanceScores[i];
        if (maxImp <= 1e-10) return;

        // Scale query weights: important channels get boosted initial attention
        // The query projection is [filters x filters]. We scale columns corresponding
        // to the input dimension indices that map to important channels.
        // Since attention operates on the last block's output (filters-dimensional),
        // and channel importance is from the INPUT channels, we apply a softer bias:
        // boost diagonal-adjacent elements proportionally to importance.
        int channelCount = Math.Min(parentImportanceScores.Length, filters);
        for (int row = 0; row < filters; row++)
        {
            for (int col = 0; col < channelCount; col++)
            {
                double boost = 1.0 + parentImportanceScores[col] / maxImp;
                attnQueryW[row * filters + col] *= boost;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Item 41 – Warm-Start Compatibility Validation
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates that the parent model's architecture configuration is compatible with
    /// the current training configuration. Logs structured warnings for any mismatches.
    /// Returns true if compatible (safe to warm-start), false if incompatible.
    /// </summary>
    internal static (bool Compatible, List<string> Warnings) ValidateWarmStartCompatibility(
        TcnSnapshotWeights? parentSnapshot, int currentFilters, int currentNumBlocks,
        bool currentUseLayerNorm, bool currentUseAttentionPool, int currentAttentionHeads)
    {
        if (parentSnapshot is null) return (true, []);

        var warnings = new List<string>();
        bool compatible = true;

        if (parentSnapshot.Filters != 0 && parentSnapshot.Filters != currentFilters)
        {
            warnings.Add($"Filter count mismatch: parent={parentSnapshot.Filters}, current={currentFilters}");
            compatible = false;
        }

        if (parentSnapshot.ConvW != null && parentSnapshot.ConvW.Length != currentNumBlocks)
        {
            warnings.Add($"Block count mismatch: parent={parentSnapshot.ConvW.Length}, current={currentNumBlocks}");
            // Partial restoration is still possible for overlapping blocks
        }

        if (parentSnapshot.UseLayerNorm != currentUseLayerNorm)
            warnings.Add($"LayerNorm mismatch: parent={parentSnapshot.UseLayerNorm}, current={currentUseLayerNorm}");

        if (parentSnapshot.UseAttentionPooling != currentUseAttentionPool)
            warnings.Add($"AttentionPooling mismatch: parent={parentSnapshot.UseAttentionPooling}, current={currentUseAttentionPool}");

        if (parentSnapshot.AttentionHeads != currentAttentionHeads && parentSnapshot.AttentionHeads > 0)
        {
            warnings.Add($"AttentionHeads mismatch: parent={parentSnapshot.AttentionHeads}, current={currentAttentionHeads}");
            if (parentSnapshot.AttnQueryW?.Length != currentFilters * currentFilters)
                compatible = false; // Can't restore attention weights with different dimensions
        }

        return (compatible, warnings);
    }
}
