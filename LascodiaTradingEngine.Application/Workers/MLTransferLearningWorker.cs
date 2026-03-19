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

    public MLTransferLearningWorker(
        ILogger<MLTransferLearningWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLTransferLearningWorker starting");
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

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var writeCtx      = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db            = writeCtx.GetDbContext();

        // Load all active models grouped by timeframe
        var activeModels = await db.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .ToListAsync(ct);

        if (activeModels.Count < 2) return;

        // Find symbols that have thin training history (last completed run had few samples)
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
            // Skip if already has an active model with sufficient history
            bool alreadyRich = activeModels.Any(m =>
                m.Symbol    == thin.Symbol &&
                m.Timeframe == thin.Timeframe &&
                m.TrainingSamples >= MaxSamplesForTransfer);

            if (alreadyRich) continue;

            // Skip if a warm-start run is already queued for this symbol/timeframe
            bool alreadyQueued = await db.Set<MLTrainingRun>()
                .AnyAsync(r => r.Symbol    == thin.Symbol
                            && r.Timeframe == thin.Timeframe
                            && r.Status    == RunStatus.Queued
                            && !r.IsDeleted, ct);

            if (alreadyQueued) continue;

            // Find best donor: same timeframe, different symbol, has model bytes
            var donor = activeModels
                .Where(m => m.Timeframe == thin.Timeframe && m.Symbol != thin.Symbol)
                .OrderByDescending(m => m.WalkForwardAvgAccuracy ?? m.DirectionAccuracy ?? 0)
                .FirstOrDefault();

            if (donor == null) continue;

            // Queue a warm-start training run referencing the donor model
            var newRun = new MLTrainingRun
            {
                Symbol            = thin.Symbol,
                Timeframe         = thin.Timeframe,
                TriggerType       = TriggerType.Scheduled,
                Status            = RunStatus.Queued,
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

        if (queued > 0)
            await db.SaveChangesAsync(ct);
    }
}
