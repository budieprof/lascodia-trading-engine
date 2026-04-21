using System.Diagnostics;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects replayable pairwise feature-product candidates from resolved live prediction logs.
/// </summary>
/// <remarks>
/// The worker prefers persisted raw model feature vectors and falls back to stored
/// SHAP/contribution vectors for legacy prediction logs. For each feature pair it compares a
/// reduced model (<c>ActualDirection ~ a + b</c>) with a full model
/// (<c>ActualDirection ~ a + b + a*b</c>) and ranks the incremental product term by partial
/// F-ratio. The selected pairs are persisted to <see cref="MLFeatureInteractionAudit"/> and
/// can be replayed by training/scoring as explicit product features.
/// </remarks>
public sealed class MLFeatureInteractionWorker : BackgroundService
{
    private const string WorkerName = nameof(MLFeatureInteractionWorker);
    private const string DistributedLockKey = "ml:feature-interaction:cycle";
    private const string RawFeatureMethod = "RawFeaturePartialF";
    private const string ShapFallbackMethod = "ShapContributionPartialF";

    private const string CK_PollSecs = "MLFeatureInteraction:PollIntervalSeconds";
    private const string CK_TopK = "MLFeatureInteraction:TopK";
    private const string CK_IncludedTopN = "MLFeatureInteraction:IncludedTopN";
    private const string CK_MinSamples = "MLFeatureInteraction:MinSamples";
    private const string CK_MaxLogsPerModel = "MLFeatureInteraction:MaxLogsPerModel";
    private const string CK_MaxFeatures = "MLFeatureInteraction:MaxFeatures";
    private const string CK_MaxModelsPerCycle = "MLFeatureInteraction:MaxModelsPerCycle";
    private const string CK_MinEffectSize = "MLFeatureInteraction:MinEffectSize";
    private const string CK_MaxQValue = "MLFeatureInteraction:MaxQValue";
    private const string CK_LockTimeoutSecs = "MLFeatureInteraction:LockTimeoutSeconds";

    private const int DefaultPollSeconds = 7 * 24 * 60 * 60;
    private const int DefaultTopK = 5;
    private const int DefaultIncludedTopN = 3;
    private const int DefaultMinSamples = 100;
    private const int DefaultMaxLogsPerModel = 1000;
    private const int DefaultMaxFeatures = MLFeatureHelper.FeatureCountV7;
    private const int DefaultMaxModelsPerCycle = 256;
    private const int DefaultLockTimeoutSeconds = 0;
    private const double DefaultMinEffectSize = 0.001;
    private const double DefaultMaxQValue = 0.20;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureInteractionWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;

    public MLFeatureInteractionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLFeatureInteractionWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureInteractionWorker started.");
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Ranks schema-aware feature-product candidates from resolved ML prediction logs.",
            TimeSpan.FromSeconds(DefaultPollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;
            var cycleStart = Stopwatch.GetTimestamp();
            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                pollSecs = await RunCycleAsync(stoppingToken);

                long durationMs = (long)Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
                _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                _metrics?.WorkerCycleDurationMs.Record(
                    durationMs,
                    new KeyValuePair<string, object?>("worker", WorkerName));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName));
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                _logger.LogError(ex, "MLFeatureInteractionWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(pollSecs, 60, 7 * 24 * 60 * 60)), stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        _logger.LogInformation("MLFeatureInteractionWorker stopping.");
    }

    internal async Task<int> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
        var config = await LoadConfigAsync(readCtx, ct);

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is not null)
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(config.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _logger.LogDebug("MLFeatureInteractionWorker: cycle skipped because distributed lock is held elsewhere.");
                return config.PollSeconds;
            }
        }
        else
        {
            _logger.LogWarning(
                "MLFeatureInteractionWorker running without IDistributedLock; duplicate audit rows are possible in multi-instance deployments.");
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                await RunCycleCoreAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }

        return config.PollSeconds;
    }

    private async Task RunCycleCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureInteractionConfig config,
        CancellationToken ct)
    {
        var models = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && m.ModelBytes != null)
            .OrderBy(m => m.Symbol)
            .ThenBy(m => m.Timeframe)
            .ThenByDescending(m => m.TrainedAt)
            .Take(config.MaxModelsPerCycle)
            .Select(m => new
            {
                m.Id,
                m.Symbol,
                m.Timeframe,
                m.ModelBytes
            })
            .ToListAsync(ct);

        _healthMonitor?.RecordBacklogDepth(WorkerName, models.Count);

        int written = 0, skipped = 0, failed = 0;
        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool didWrite = await ProcessModelAsync(
                    readCtx,
                    writeCtx,
                    model.Id,
                    model.Symbol,
                    model.Timeframe,
                    model.ModelBytes!,
                    config,
                    ct);

                if (didWrite) written++;
                else skipped++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex,
                    "MLFeatureInteractionWorker: failed model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }

        _logger.LogInformation(
            "MLFeatureInteractionWorker cycle complete: written={Written}, skipped={Skipped}, failed={Failed}, models={Total}.",
            written, skipped, failed, models.Count);
    }

    private async Task<bool> ProcessModelAsync(
        DbContext readCtx,
        DbContext writeCtx,
        long modelId,
        string symbol,
        Timeframe timeframe,
        byte[] modelBytes,
        FeatureInteractionConfig config,
        CancellationToken ct)
    {
        var snapshot = TryDeserializeSnapshot(modelBytes, modelId);
        if (snapshot is null)
            return false;

        int resolvedFeatures = snapshot.ResolveExpectedInputFeatures();
        int baseFeatureCount = snapshot.InteractionBaseFeatureCount > 0
            ? snapshot.InteractionBaseFeatureCount
            : resolvedFeatures;
        baseFeatureCount = Math.Min(baseFeatureCount, resolvedFeatures);
        if (baseFeatureCount < 2 || baseFeatureCount > MLFeatureHelper.MaxAllowedFeatureCount)
            return false;

        int schemaVersion = snapshot.ResolveFeatureSchemaVersion();
        int featureLimit = Math.Clamp(config.MaxFeatures, 2, baseFeatureCount);
        var featureNames = ResolveFeatureNames(snapshot, baseFeatureCount);

        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId == modelId
                     && !l.IsDeleted
                     && l.ActualDirection.HasValue
                     && (l.RawFeaturesJson != null || l.ShapValuesJson != null))
            .OrderByDescending(l => l.PredictedAt)
            .Take(config.MaxLogsPerModel)
            .Select(l => new PredictionFeatureLog(
                l.RawFeaturesJson,
                l.ShapValuesJson,
                l.ActualDirection!.Value))
            .ToListAsync(ct);

        if (logs.Count < config.MinSamples)
        {
            _logger.LogDebug(
                "MLFeatureInteractionWorker: model {ModelId} has {Rows}/{Min} feature rows before parsing.",
                modelId, logs.Count, config.MinSamples);
            return false;
        }

        int rawRows = logs.Count(l => l.RawFeaturesJson is not null);
        int shapRows = logs.Count - rawRows;
        var rawParse = BuildInteractionRows(
            logs,
            useRawFeatures: true,
            baseFeatureCount,
            featureLimit);
        var selectedParse = rawParse.Rows.Count >= config.MinSamples
            ? rawParse
            : BuildInteractionRows(
                logs,
                useRawFeatures: false,
                baseFeatureCount,
                featureLimit);

        string method = selectedParse.UsedRawFeatures ? RawFeatureMethod : ShapFallbackMethod;
        var rows = selectedParse.Rows;

        if (rows.Count < config.MinSamples)
        {
            _logger.LogDebug(
                "MLFeatureInteractionWorker: model {ModelId} usable rows {Rows}/{Min}; method={Method}, rawRows={RawRows}, shapRows={ShapRows}, malformed={Malformed}, wrongShape={WrongShape}, nonFinite={NonFinite}.",
                modelId, rows.Count, config.MinSamples, method, rawRows, shapRows,
                selectedParse.Malformed, selectedParse.WrongShape, selectedParse.NonFinite);
            return false;
        }

        var candidates = ScorePairs(rows, featureLimit);
        ApplyBenjaminiHochberg(candidates);

        var topK = candidates
            .Where(c => c.Score > 0 && c.EffectSize >= config.MinEffectSize && c.QValue <= config.MaxQValue)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.EffectSize)
            .Take(config.TopK)
            .ToList();

        await using var tx = await writeCtx.Database.BeginTransactionAsync(ct);
        var old = await writeCtx.Set<MLFeatureInteractionAudit>()
            .Where(a => a.MLModelId == modelId && !a.IsDeleted)
            .ToListAsync(ct);
        foreach (var audit in old)
            audit.IsDeleted = true;

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        for (int rank = 0; rank < topK.Count; rank++)
        {
            var candidate = topK[rank];
            writeCtx.Set<MLFeatureInteractionAudit>().Add(new MLFeatureInteractionAudit
            {
                MLModelId = modelId,
                Symbol = symbol,
                Timeframe = timeframe,
                FeatureIndexA = candidate.A,
                FeatureNameA = featureNames[candidate.A],
                FeatureIndexB = candidate.B,
                FeatureNameB = featureNames[candidate.B],
                FeatureSchemaVersion = schemaVersion,
                BaseFeatureCount = baseFeatureCount,
                SampleCount = rows.Count,
                Method = method,
                InteractionScore = candidate.Score,
                EffectSize = candidate.EffectSize,
                PValue = candidate.PValue,
                QValue = candidate.QValue,
                Rank = rank + 1,
                IsIncludedAsFeature = rank < config.IncludedTopN,
                ComputedAt = nowUtc
            });
        }

        await writeCtx.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "MLFeatureInteractionWorker: {Symbol}/{Timeframe} model={ModelId} method={Method} rows={Rows}, pairsTested={Pairs}, written={Written}, top={TopA}x{TopB}, score={Score:F3}, q={Q:F4}.",
            symbol, timeframe, modelId, method, rows.Count, candidates.Count, topK.Count,
            topK.Count > 0 ? featureNames[topK[0].A] : "?",
            topK.Count > 0 ? featureNames[topK[0].B] : "?",
            topK.Count > 0 ? topK[0].Score : 0.0,
            topK.Count > 0 ? topK[0].QValue : 1.0);

        return true;
    }

    internal static List<InteractionCandidate> ScorePairs(IReadOnlyList<InteractionRow> rows, int featureCount)
    {
        var candidates = new List<InteractionCandidate>(featureCount * (featureCount - 1) / 2);
        for (int a = 0; a < featureCount; a++)
        for (int b = a + 1; b < featureCount; b++)
        {
            var score = ComputePartialF(rows, a, b);
            if (score.Score > 0 && double.IsFinite(score.Score))
                candidates.Add(score);
        }

        return candidates;
    }

    internal static InteractionCandidate ComputePartialF(IReadOnlyList<InteractionRow> rows, int a, int b)
    {
        int n = rows.Count;
        if (n < 8)
            return new InteractionCandidate(a, b, 0, 0, 1, 1);

        double sseReduced = ComputeSse(rows, a, b, includeInteraction: false);
        double sseFull = ComputeSse(rows, a, b, includeInteraction: true);
        if (!double.IsFinite(sseReduced) || !double.IsFinite(sseFull) || sseFull <= 1e-12 || sseReduced <= sseFull)
            return new InteractionCandidate(a, b, 0, 0, 1, 1);

        double yMean = rows.Average(r => r.Label);
        double sst = rows.Sum(r => (r.Label - yMean) * (r.Label - yMean));
        if (sst <= 1e-12)
            return new InteractionCandidate(a, b, 0, 0, 1, 1);

        double f = (sseReduced - sseFull) / (sseFull / (n - 4));
        double r2Reduced = 1.0 - sseReduced / sst;
        double r2Full = 1.0 - sseFull / sst;
        double effectSize = Math.Max(0.0, r2Full - r2Reduced);
        double p = FSurvival(f, 1, n - 4);

        return new InteractionCandidate(a, b, f, effectSize, p, 1);
    }

    private static InteractionRowParseResult BuildInteractionRows(
        IReadOnlyList<PredictionFeatureLog> logs,
        bool useRawFeatures,
        int baseFeatureCount,
        int featureLimit)
    {
        int malformed = 0, wrongShape = 0, nonFinite = 0;
        var rows = new List<InteractionRow>(logs.Count);
        foreach (var log in logs)
        {
            var json = useRawFeatures ? log.RawFeaturesJson : log.ShapValuesJson;
            if (json is null)
                continue;

            double[]? values;
            try
            {
                values = JsonSerializer.Deserialize<double[]>(json, JsonOptions);
            }
            catch (JsonException)
            {
                malformed++;
                continue;
            }

            if (values is null || values.Length < baseFeatureCount)
            {
                wrongShape++;
                continue;
            }

            var row = new double[featureLimit];
            bool finite = true;
            for (int i = 0; i < featureLimit; i++)
            {
                double value = values[i];
                if (!double.IsFinite(value))
                {
                    finite = false;
                    break;
                }
                row[i] = value;
            }

            if (!finite)
            {
                nonFinite++;
                continue;
            }

            rows.Add(new InteractionRow(row, log.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0));
        }

        return new InteractionRowParseResult(rows, useRawFeatures, malformed, wrongShape, nonFinite);
    }

    private static double ComputeSse(IReadOnlyList<InteractionRow> rows, int a, int b, bool includeInteraction)
    {
        int p = includeInteraction ? 4 : 3;
        var xtx = new double[p, p];
        var xty = new double[p];

        Span<double> x = stackalloc double[4];
        for (int r = 0; r < rows.Count; r++)
        {
            double xa = rows[r].Values[a];
            double xb = rows[r].Values[b];
            x[0] = 1.0;
            x[1] = xa;
            x[2] = xb;
            if (includeInteraction)
                x[3] = xa * xb;

            for (int i = 0; i < p; i++)
            {
                xty[i] += x[i] * rows[r].Label;
                for (int j = 0; j < p; j++)
                    xtx[i, j] += x[i] * x[j];
            }
        }

        var beta = SolveLinearSystem(xtx, xty);
        if (beta is null)
            return double.PositiveInfinity;

        double sse = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            double xa = rows[r].Values[a];
            double xb = rows[r].Values[b];
            x[0] = 1.0;
            x[1] = xa;
            x[2] = xb;
            if (includeInteraction)
                x[3] = xa * xb;

            double yHat = 0;
            for (int i = 0; i < p; i++)
                yHat += beta[i] * x[i];
            double e = rows[r].Label - yHat;
            sse += e * e;
        }

        return sse;
    }

    private static double[]? SolveLinearSystem(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                m[i, j] = a[i, j];
            m[i, n] = b[i];
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col]))
                    pivot = row;

            if (Math.Abs(m[pivot, col]) < 1e-10)
                return null;

            if (pivot != col)
                for (int j = col; j <= n; j++)
                    (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);

            double div = m[col, col];
            for (int j = col; j <= n; j++)
                m[col, j] /= div;

            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                double factor = m[row, col];
                for (int j = col; j <= n; j++)
                    m[row, j] -= factor * m[col, j];
            }
        }

        var result = new double[n];
        for (int i = 0; i < n; i++)
            result[i] = m[i, n];
        return result;
    }

    private static void ApplyBenjaminiHochberg(List<InteractionCandidate> candidates)
    {
        if (candidates.Count == 0)
            return;

        var ordered = candidates
            .Select((c, i) => (Candidate: c, OriginalIndex: i))
            .OrderBy(x => x.Candidate.PValue)
            .ToArray();

        double runningMin = 1.0;
        int m = ordered.Length;
        for (int i = m - 1; i >= 0; i--)
        {
            double q = Math.Min(runningMin, ordered[i].Candidate.PValue * m / (i + 1));
            runningMin = Math.Clamp(q, 0.0, 1.0);
            var c = ordered[i].Candidate;
            candidates[ordered[i].OriginalIndex] = c with { QValue = runningMin };
        }
    }

    private static double FSurvival(double f, double df1, double df2)
    {
        if (!double.IsFinite(f) || f <= 0 || df1 <= 0 || df2 <= 0)
            return 1.0;

        double x = df2 / (df2 + df1 * f);
        return RegularizedIncompleteBeta(x, df2 / 2.0, df1 / 2.0);
    }

    private static double RegularizedIncompleteBeta(double x, double a, double b)
    {
        if (x <= 0) return 0.0;
        if (x >= 1) return 1.0;

        double front = Math.Exp(
            LogGamma(a + b) - LogGamma(a) - LogGamma(b)
            + a * Math.Log(x)
            + b * Math.Log(1.0 - x));

        if (x < (a + 1.0) / (a + b + 2.0))
            return Math.Clamp(front * BetaContinuedFraction(a, b, x) / a, 0.0, 1.0);

        double complement = front * BetaContinuedFraction(b, a, 1.0 - x) / b;
        return Math.Clamp(1.0 - complement, 0.0, 1.0);
    }

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const int maxIterations = 200;
        const double epsilon = 3.0e-14;
        const double fpMin = 1.0e-300;

        double qab = a + b;
        double qap = a + 1.0;
        double qam = a - 1.0;
        double c = 1.0;
        double d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < fpMin)
            d = fpMin;
        d = 1.0 / d;
        double h = d;

        for (int m = 1; m <= maxIterations; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < fpMin)
                d = fpMin;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < fpMin)
                c = fpMin;
            d = 1.0 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < fpMin)
                d = fpMin;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < fpMin)
                c = fpMin;
            d = 1.0 / d;
            double delta = d * c;
            h *= delta;

            if (Math.Abs(delta - 1.0) <= epsilon)
                break;
        }

        return h;
    }

    private static double LogGamma(double x)
    {
        double[] coefficients =
        [
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -0.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7
        ];

        if (x < 0.5)
            return Math.Log(Math.PI) - Math.Log(Math.Sin(Math.PI * x)) - LogGamma(1.0 - x);

        x -= 1.0;
        double a = 0.99999999999980993;
        for (int i = 0; i < coefficients.Length; i++)
            a += coefficients[i] / (x + i + 1.0);

        double t = x + coefficients.Length - 0.5;
        return 0.9189385332046727 + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    private static string[] ResolveFeatureNames(ModelSnapshot snapshot, int featureCount)
    {
        if (snapshot.Features.Length >= featureCount)
            return snapshot.Features.Take(featureCount).ToArray();
        return MLFeatureHelper.ResolveFeatureNames(featureCount);
    }

    private ModelSnapshot? TryDeserializeSnapshot(byte[] modelBytes, long modelId)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelSnapshot>(modelBytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "MLFeatureInteractionWorker: failed to deserialize snapshot for model {ModelId}.",
                modelId);
            return null;
        }
    }

    private static async Task<FeatureInteractionConfig> LoadConfigAsync(DbContext ctx, CancellationToken ct)
    {
        var values = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("MLFeatureInteraction:"))
            .Select(c => new { c.Key, c.Value })
            .ToDictionaryAsync(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase, ct);

        return new FeatureInteractionConfig(
            PollSeconds: GetInt(values, CK_PollSecs, DefaultPollSeconds, 60, 7 * 24 * 60 * 60),
            TopK: GetInt(values, CK_TopK, DefaultTopK, 1, 20),
            IncludedTopN: GetInt(values, CK_IncludedTopN, DefaultIncludedTopN, 0, 10),
            MinSamples: GetInt(values, CK_MinSamples, DefaultMinSamples, 50, 100_000),
            MaxLogsPerModel: GetInt(values, CK_MaxLogsPerModel, DefaultMaxLogsPerModel, 100, 100_000),
            MaxFeatures: GetInt(values, CK_MaxFeatures, DefaultMaxFeatures, 2, MLFeatureHelper.MaxAllowedFeatureCount),
            MaxModelsPerCycle: GetInt(values, CK_MaxModelsPerCycle, DefaultMaxModelsPerCycle, 1, 10_000),
            MinEffectSize: GetDouble(values, CK_MinEffectSize, DefaultMinEffectSize, 0.0, 1.0),
            MaxQValue: GetDouble(values, CK_MaxQValue, DefaultMaxQValue, 0.0, 1.0),
            LockTimeoutSeconds: GetInt(values, CK_LockTimeoutSecs, DefaultLockTimeoutSeconds, 0, 300));
    }

    private static int GetInt(Dictionary<string, string> values, string key, int fallback, int min, int max)
        => values.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    private static double GetDouble(Dictionary<string, string> values, string key, double fallback, double min, double max)
        => values.TryGetValue(key, out var raw) && double.TryParse(raw, out var parsed) && double.IsFinite(parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    public readonly record struct InteractionRow(double[] Values, double Label);

    public readonly record struct InteractionCandidate(
        int A,
        int B,
        double Score,
        double EffectSize,
        double PValue,
        double QValue);

    private sealed record PredictionFeatureLog(
        string? RawFeaturesJson,
        string? ShapValuesJson,
        TradeDirection ActualDirection);

    private sealed record InteractionRowParseResult(
        List<InteractionRow> Rows,
        bool UsedRawFeatures,
        int Malformed,
        int WrongShape,
        int NonFinite);

    private sealed record FeatureInteractionConfig(
        int PollSeconds,
        int TopK,
        int IncludedTopN,
        int MinSamples,
        int MaxLogsPerModel,
        int MaxFeatures,
        int MaxModelsPerCycle,
        double MinEffectSize,
        double MaxQValue,
        int LockTimeoutSeconds);
}
