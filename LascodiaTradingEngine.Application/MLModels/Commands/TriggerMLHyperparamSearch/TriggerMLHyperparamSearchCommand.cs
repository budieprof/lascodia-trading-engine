using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Commands.TriggerMLHyperparamSearch;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queues a random hyperparameter search over key ML training parameters.
/// Creates <see cref="SearchCandidates"/> <see cref="MLTrainingRun"/> records, each carrying
/// a unique hyperparameter combination encoded in <c>HyperparamConfigJson</c>.
/// <see cref="MLTrainingWorker"/> reads this field and overrides the EngineConfig defaults
/// with the per-run values, enabling fully automated parallel exploration.
///
/// After all candidate runs complete, <c>MLHyperparamBestPickWorker</c> picks the run with
/// the best validation accuracy and queues a final "promotion" training run using those
/// hyperparameters.
/// </summary>
public class TriggerMLHyperparamSearchCommand : IRequest<ResponseData<int>>
{
    /// <summary>Currency pair to optimise (e.g. "EURUSD").</summary>
    public required string Symbol    { get; set; }

    /// <summary>Chart timeframe to optimise.</summary>
    public required string Timeframe { get; set; }

    /// <summary>
    /// How many days of historical candles to train on for each candidate run.
    /// Defaults to 365 days.
    /// </summary>
    public int TrainingDays { get; set; } = 365;

    /// <summary>
    /// Number of hyperparameter combinations to try. Defaults to 12.
    /// Higher values improve the chance of finding the optimum but increase compute time.
    /// </summary>
    public int SearchCandidates { get; set; } = 12;

    /// <summary>
    /// When <c>true</c>, candidates are selected via Gaussian Process UCB (GP-UCB) using
    /// past completed runs as observations, rather than uniform random sampling.
    /// Requires ≥ 3 completed runs for the same symbol/timeframe; falls back to random
    /// search when fewer observations are available.
    /// </summary>
    public bool UseGPSearch { get; set; } = false;

    /// <summary>
    /// When set, the HP search trains regime-specific sub-models scoped to this regime name
    /// (e.g. "Trending", "Ranging"). Each candidate run will produce a model with
    /// <c>MLModel.RegimeScope</c> set to this value, enabling per-regime HP optimisation.
    /// Null = search for the global (unscoped) model.
    /// </summary>
    public string? RegimeScope { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class TriggerMLHyperparamSearchCommandValidator : AbstractValidator<TriggerMLHyperparamSearchCommand>
{
    public TriggerMLHyperparamSearchCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol is required")
            .MaximumLength(10);

        RuleFor(x => x.Timeframe)
            .NotEmpty().WithMessage("Timeframe is required");

        RuleFor(x => x.TrainingDays)
            .InclusiveBetween(30, 1825).WithMessage("TrainingDays must be between 30 and 1825");

        RuleFor(x => x.SearchCandidates)
            .InclusiveBetween(2, 50).WithMessage("SearchCandidates must be between 2 and 50");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class TriggerMLHyperparamSearchCommandHandler
    : IRequestHandler<TriggerMLHyperparamSearchCommand, ResponseData<int>>
{
    // ── Discrete search space ─────────────────────────────────────────────────
    private static readonly int[]    KValues       = [3, 5, 7, 9];
    private static readonly double[] LearningRates = [0.001, 0.005, 0.01, 0.05];
    private static readonly double[] L2Lambdas     = [0.0001, 0.001, 0.005, 0.01];
    private static readonly double[] DecayLambdas  = [1.0, 1.5, 2.0, 3.0];
    private static readonly int[]    EpochCounts   = [100, 150, 200];
    private static readonly int[]    EmbargoBars   = [20, 30, 40];

    // Minimum past observations needed before trusting the GP surrogate
    private const int MinObservationsForGP = 3;

    private readonly IWriteApplicationDbContext _context;

    public TriggerMLHyperparamSearchCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<int>> Handle(
        TriggerMLHyperparamSearchCommand request,
        CancellationToken                cancellationToken)
    {
        if (!Enum.TryParse<Timeframe>(request.Timeframe, ignoreCase: true, out var tf))
            return ResponseData<int>.Init(0, false, $"Unknown timeframe: {request.Timeframe}", "-11");

        var db      = _context.GetDbContext();
        var now     = DateTime.UtcNow;
        var batchId = Guid.NewGuid();
        var rng     = new Random();
        var runs    = new List<MLTrainingRun>(request.SearchCandidates);

        bool alreadyQueued = await db.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == request.Symbol &&
                           r.Timeframe == tf             &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                      cancellationToken);

        if (alreadyQueued)
            return ResponseData<int>.Init(0, false,
                "A training run is already queued or running for this symbol/timeframe.", "-11");

        // ── Build all enumerable candidates upfront ───────────────────────────
        var allCandidates = EnumerateAllCandidates();

        // ── GP-UCB candidate selection ────────────────────────────────────────
        IEnumerable<int> selectedIndices;
        bool usedGP = false;

        if (request.UseGPSearch)
        {
            var pastRuns = await db.Set<MLTrainingRun>()
                .Where(r => r.Symbol    == request.Symbol &&
                            r.Timeframe == tf             &&
                            r.Status    == RunStatus.Completed &&
                            r.HyperparamConfigJson != null)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            if (pastRuns.Count >= MinObservationsForGP)
            {
                selectedIndices = SelectViaGP(allCandidates, pastRuns, request.SearchCandidates, rng);
                usedGP = true;
            }
            else
            {
                selectedIndices = SelectRandom(allCandidates.Length, request.SearchCandidates, rng);
            }
        }
        else
        {
            selectedIndices = SelectRandom(allCandidates.Length, request.SearchCandidates, rng);
        }

        // ── Build runs from selected candidate indices ────────────────────────
        int seq = 1;
        foreach (int idx in selectedIndices)
        {
            var c = allCandidates[idx];
            var hp = new HyperparamCandidate(
                K:                   c.K,
                LearningRate:        c.LearningRate,
                L2Lambda:            c.L2Lambda,
                TemporalDecayLambda: c.DecayLambda,
                MaxEpochs:           c.Epochs,
                EmbargoBarCount:     c.Embargo,
                SearchBatchId:       batchId,
                CandidateIndex:      seq,
                TotalCandidates:     request.SearchCandidates,
                RegimeScope:         request.RegimeScope);

            runs.Add(new MLTrainingRun
            {
                Symbol               = request.Symbol,
                Timeframe            = tf,
                TriggerType          = TriggerType.Manual,
                Status               = RunStatus.Queued,
                FromDate             = now.AddDays(-request.TrainingDays),
                ToDate               = now,
                StartedAt            = now,
                HyperparamConfigJson = JsonSerializer.Serialize(hp),
            });
            seq++;
        }

        db.Set<MLTrainingRun>().AddRange(runs);
        await _context.SaveChangesAsync(cancellationToken);

        string strategy = usedGP ? "GP-UCB" : "random";
        return ResponseData<int>.Init(
            runs.Count,
            true,
            $"Queued {runs.Count} hyperparameter search candidates via {strategy} (batch={batchId}).",
            "00");
    }

    // ── Candidate enumeration ─────────────────────────────────────────────────

    private record RawCandidate(int K, double LearningRate, double L2Lambda,
                                double DecayLambda, int Epochs, int Embargo);

    private static RawCandidate[] EnumerateAllCandidates()
    {
        var list = new List<RawCandidate>(
            KValues.Length * LearningRates.Length * L2Lambdas.Length *
            DecayLambdas.Length * EpochCounts.Length * EmbargoBars.Length);

        foreach (var k   in KValues)
        foreach (var lr  in LearningRates)
        foreach (var l2  in L2Lambdas)
        foreach (var d   in DecayLambdas)
        foreach (var ep  in EpochCounts)
        foreach (var emb in EmbargoBars)
            list.Add(new RawCandidate(k, lr, l2, d, ep, emb));

        return [.. list];
    }

    // ── Random selection ──────────────────────────────────────────────────────

    private static IEnumerable<int> SelectRandom(int poolSize, int n, Random rng)
        => Enumerable.Range(0, poolSize)
                     .OrderBy(_ => rng.Next())
                     .Take(n);

    // ── GP-UCB selection ──────────────────────────────────────────────────────

    private static IEnumerable<int> SelectViaGP(
        RawCandidate[]    candidates,
        List<MLTrainingRun> pastRuns,
        int               n,
        Random            rng)
    {
        // Encode each candidate into a normalised [0,1] feature vector
        var encodedCandidates = candidates.Select(EncodeCandidate).ToArray();

        // Build GP observations from past runs
        var obsList  = new List<double[]>();
        var scoreList = new List<double>();

        foreach (var run in pastRuns)
        {
            HyperparamOverrides? overrides = null;
            try { overrides = System.Text.Json.JsonSerializer.Deserialize<HyperparamOverrides>(run.HyperparamConfigJson!); }
            catch { continue; }

            if (overrides is null) continue;

            double[] encoded = EncodeOverrides(overrides);
            double   score   = CompositeScore(run);
            obsList.Add(encoded);
            scoreList.Add(score);
        }

        if (obsList.Count < MinObservationsForGP)
        {
            // Not enough usable observations — fall back to random
            return SelectRandom(candidates.Length, n, rng);
        }

        var gp = new GaussianProcessSurrogate(lengthScale: 0.5, noise: 1e-4, kappa: 2.0);
        gp.Fit([.. obsList], [.. scoreList]);

        // Return top-n distinct indices by UCB, then fill remainder randomly
        var ranked  = gp.RankByUcb(encodedCandidates).Take(n * 2).Distinct().ToList();
        var topN    = ranked.Take(n).ToList();

        // If GP returns fewer than n (e.g. all tied), pad with random
        if (topN.Count < n)
        {
            var used = new HashSet<int>(topN);
            foreach (int r in SelectRandom(candidates.Length, n * 3, rng))
            {
                if (topN.Count >= n) break;
                if (used.Add(r)) topN.Add(r);
            }
        }

        return topN;
    }

    // ── Hyperparameter encoding ───────────────────────────────────────────────

    private static double[] EncodeCandidate(RawCandidate c) =>
    [
        Normalise(c.K,             3,                          9),
        NormaliseLog(c.LearningRate, 0.001,                    0.05),
        NormaliseLog(c.L2Lambda,     0.0001,                   0.01),
        Normalise(c.DecayLambda,   1.0,                        3.0),
        Normalise(c.Epochs,        100,                        200),
        Normalise(c.Embargo,       20,                         40),
    ];

    private static double[] EncodeOverrides(HyperparamOverrides o) =>
    [
        Normalise(o.K                   ?? 5,     3,      9),
        NormaliseLog(o.LearningRate     ?? 0.01,  0.001,  0.05),
        NormaliseLog(o.L2Lambda         ?? 0.001, 0.0001, 0.01),
        Normalise(o.TemporalDecayLambda ?? 2.0,   1.0,    3.0),
        Normalise(o.MaxEpochs           ?? 150,   100,    200),
        Normalise(o.EmbargoBarCount     ?? 30,    20,     40),
    ];

    private static double Normalise(double v, double min, double max)
        => max > min ? Math.Clamp((v - min) / (max - min), 0.0, 1.0) : 0.5;

    private static double NormaliseLog(double v, double min, double max)
    {
        double lv   = Math.Log(Math.Max(v, min));
        double lMin = Math.Log(min);
        double lMax = Math.Log(max);
        return lMax > lMin ? Math.Clamp((lv - lMin) / (lMax - lMin), 0.0, 1.0) : 0.5;
    }

    // ── Composite score ───────────────────────────────────────────────────────

    private static double CompositeScore(MLTrainingRun run)
    {
        double acc    = (double)run.DirectionAccuracy;
        double ev     = (double)run.ExpectedValue;
        double sharpe = (double)run.SharpeRatio;

        // Normalise each component to [0,1] using soft-max transforms
        double accScore    = Math.Clamp(acc,  0.0, 1.0);
        double evScore     = Math.Clamp((ev + 0.5) / 1.0, 0.0, 1.0);
        double sharpeScore = Math.Clamp((sharpe + 1.0) / 4.0, 0.0, 1.0);

        return 0.5 * accScore + 0.3 * sharpeScore + 0.2 * evScore;
    }

    // ── DTO ───────────────────────────────────────────────────────────────────

    private record HyperparamCandidate(
        int     K,
        double  LearningRate,
        double  L2Lambda,
        double  TemporalDecayLambda,
        int     MaxEpochs,
        int     EmbargoBarCount,
        Guid    SearchBatchId,
        int     CandidateIndex,
        int     TotalCandidates,
        string? RegimeScope = null);
}
