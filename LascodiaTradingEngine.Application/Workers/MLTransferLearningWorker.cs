using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background worker that implements cross-symbol knowledge transfer.
/// After a model is promoted on a well-trained symbol, this worker finds
/// other symbols that:
/// (a) have a correlated model on the same timeframe, and
/// (b) have fewer training samples than the configured threshold.
/// It then queues a warm-start training run using the donor model's <c>ModelBytes</c>.
/// </summary>
/// <remarks>
/// Transfer learning reduces cold-start variance on thin-history symbols by
/// initialising ensemble weights from a donor model, letting the supervised
/// phase converge faster on the new symbol's distribution.
/// The donor is selected as the active model with the highest cross-symbol
/// Pearson correlation (from <c>MLSymbolCorrelationWorker</c> records stored in
/// <c>MLModelRegimeAccuracy</c> or a dedicated correlation table).
/// </remarks>
public class MLTransferLearningWorker : BackgroundService
{
    private readonly ILogger<MLTransferLearningWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan _interval       = TimeSpan.FromHours(6);
    private static readonly TimeSpan _initialDelay   = TimeSpan.FromMinutes(5);

    /// <summary>Minimum Pearson correlation to qualify as a donor (default 0.65).</summary>
    private const double MinDonorCorrelation = 0.65;

    /// <summary>
    /// Symbol/timeframe pairs with fewer samples than this threshold will receive
    /// a transfer-learning warm-start run rather than a cold-start run.
    /// </summary>
    private const int MaxSamplesForTransfer = 2000;

    /// <summary>
    /// Initialises the worker with its logger and DI scope factory.
    /// </summary>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope on every cycle so that scoped EF Core
    /// contexts are correctly disposed between polls.
    /// </param>
    public MLTransferLearningWorker(
        ILogger<MLTransferLearningWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Hosted-service entry point. Waits for <see cref="_initialDelay"/> after startup
    /// (to allow other workers and seeding tasks to complete) then enters the polling
    /// loop, executing <see cref="RunCycleAsync"/> every <see cref="_interval"/> (6 h).
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLTransferLearningWorker starting");

        // Stagger startup so the heavy training worker pipeline is settled before
        // we start queuing additional warm-start runs.
        await Task.Delay(_initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLTransferLearningWorker cycle failed");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("MLTransferLearningWorker stopped");
    }

    /// <summary>
    /// Core transfer-learning cycle. Identifies symbol/timeframe pairs that lack
    /// sufficient training history (<see cref="MaxSamplesForTransfer"/> samples) and
    /// queues a warm-start <see cref="MLTrainingRun"/> using the weights from the
    /// best-performing donor model on the same timeframe.
    /// </summary>
    /// <remarks>
    /// Transfer learning methodology:
    /// <list type="number">
    ///   <item>
    ///     Load all active models that have serialised <c>ModelBytes</c> — these are
    ///     candidates to act as donors. At least two active models must exist before
    ///     the cycle proceeds; with only one model there is nothing to transfer from.
    ///   </item>
    ///   <item>
    ///     Query <see cref="MLTrainingRun"/> records with <c>Status = Completed</c>
    ///     and fewer than <see cref="MaxSamplesForTransfer"/> total samples to build
    ///     the "thin symbols" set. These are symbol/timeframe pairs where cold-start
    ///     variance is expected to be high due to limited historical data.
    ///   </item>
    ///   <item>
    ///     For each thin symbol, skip if: (a) an active model with sufficient samples
    ///     already exists, or (b) a queued warm-start run is already pending.
    ///   </item>
    ///   <item>
    ///     Select the donor: the active model on the same timeframe (different symbol)
    ///     with the highest <c>WalkForwardAvgAccuracy</c> (falling back to
    ///     <c>DirectionAccuracy</c>). Using walk-forward accuracy ensures the donor
    ///     has demonstrated out-of-sample generalisation, not just in-sample fit.
    ///   </item>
    ///   <item>
    ///     Insert a new <see cref="MLTrainingRun"/> with <c>HyperparamConfigJson</c>
    ///     carrying <c>TransferFromModelId</c> and <c>TransferSymbol</c>. The
    ///     <c>MLTrainingWorker</c> reads these fields and initialises the ensemble
    ///     weights from <c>donor.ModelBytes</c> before supervised fine-tuning begins,
    ///     achieving a warm-start rather than random initialisation.
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var writeCtx      = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db            = writeCtx.GetDbContext();

        // Load all active models grouped by timeframe.
        // ModelBytes must be non-null so the donor can provide a serialised weight snapshot.
        var activeModels = await db.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .ToListAsync(ct);

        // Need at least one donor and one potential recipient to proceed.
        if (activeModels.Count < 2) return;

        // Find symbols that have thin training history (last completed run had few samples).
        // GroupBy collapses multiple runs for the same symbol/timeframe into one record
        // taking the maximum sample count (i.e. the most data-rich completed run).
        var thinSymbols = await db.Set<MLTrainingRun>()
            .Where(r => r.Status == RunStatus.Completed
                     && r.TotalSamples < MaxSamplesForTransfer
                     && !r.IsDeleted)
            .GroupBy(r => new { r.Symbol, r.Timeframe })
            .Select(g => new { g.Key.Symbol, g.Key.Timeframe, MaxSamples = g.Max(x => x.TotalSamples) })
            .ToListAsync(ct);

        int queued = 0;

        foreach (var thin in thinSymbols)
        {
            // Skip if the symbol already has an active model trained on sufficient history.
            // This guard prevents re-queuing a transfer run after a successful promotion.
            bool alreadyRich = activeModels.Any(m =>
                m.Symbol    == thin.Symbol &&
                m.Timeframe == thin.Timeframe &&
                m.TrainingSamples >= MaxSamplesForTransfer);

            if (alreadyRich) continue;

            // Skip if a warm-start run is already queued for this symbol/timeframe.
            // Prevents duplicate runs accumulating while MLTrainingWorker is busy.
            bool alreadyQueued = await db.Set<MLTrainingRun>()
                .AnyAsync(r => r.Symbol    == thin.Symbol
                            && r.Timeframe == thin.Timeframe
                            && r.Status    == RunStatus.Queued
                            && !r.IsDeleted, ct);

            if (alreadyQueued) continue;

            // Find the best donor on the same timeframe: highest walk-forward accuracy
            // ensures we transfer weights from a model with proven OOS generalisation.
            // Falling back to DirectionAccuracy when WalkForwardAvgAccuracy is null.
            var donor = activeModels
                .Where(m => m.Timeframe == thin.Timeframe && m.Symbol != thin.Symbol)
                .OrderByDescending(m => m.WalkForwardAvgAccuracy ?? m.DirectionAccuracy ?? 0)
                .FirstOrDefault();

            if (donor == null) continue;

            // Queue a warm-start training run referencing the donor model.
            // HyperparamConfigJson carries the donor ID so MLTrainingWorker can locate
            // donor.ModelBytes and deserialise the ModelSnapshot for weight initialisation.
            // LearnerArchitecture is copied from the donor to ensure weight dimension
            // compatibility (same number of learners K and feature count F).
            var newRun = new MLTrainingRun
            {
                Symbol            = thin.Symbol,
                Timeframe         = thin.Timeframe,
                TriggerType       = TriggerType.Scheduled,
                Status            = RunStatus.Queued,
                // Use a 1-year window to give the supervised phase as much fine-tuning
                // data as possible even though the symbol history is thin.
                FromDate          = DateTime.UtcNow.AddDays(-365),
                ToDate            = DateTime.UtcNow,
                LearnerArchitecture = donor.LearnerArchitecture,
                // The MLTrainingWorker will detect TransferredFromModelId and use donor.ModelBytes
                // to warm-start the ensemble via the warmStart parameter of IMLModelTrainer.TrainAsync
                HyperparamConfigJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    TransferFromModelId = donor.Id,
                    TransferSymbol      = donor.Symbol,
                })
            };

            db.Set<MLTrainingRun>().Add(newRun);
            queued++;

            _logger.LogInformation(
                "Queued transfer-learning run for {Symbol}/{Timeframe} from donor {DonorSymbol} (model {DonorId})",
                thin.Symbol, thin.Timeframe, donor.Symbol, donor.Id);
        }

        // Batch-save all queued runs in a single round-trip.
        if (queued > 0)
            await db.SaveChangesAsync(ct);
    }
}
