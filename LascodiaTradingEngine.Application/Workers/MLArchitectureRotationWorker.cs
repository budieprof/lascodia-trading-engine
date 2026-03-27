using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Ensures every eligible <see cref="LearnerArchitecture"/> gets at least N training runs
/// per active symbol/timeframe within a configurable rolling window. This counteracts UCB1
/// selection bias toward early-adopted architectures and guarantees all trainers get a fair
/// chance to compete.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>Query all active MLModels to get distinct (Symbol, Timeframe) pairs.</item>
///   <item>For each pair, count recent runs per architecture within the window.</item>
///   <item>Queue one run per under-represented architecture (respecting cooldown).</item>
/// </list>
///
/// Configuration (read from EngineConfig):
/// <list type="bullet">
///   <item><c>MLArchitectureRotation:PollIntervalSeconds</c> — default 7200 (2h)</item>
///   <item><c>MLArchitectureRotation:MinRunsPerWindow</c> — default 2</item>
///   <item><c>MLArchitectureRotation:WindowDays</c> — default 7</item>
///   <item><c>MLArchitectureRotation:CooldownMinutes</c> — default 60</item>
/// </list>
/// </summary>
public sealed class MLArchitectureRotationWorker : BackgroundService
{
    private const string CK_PollSecs    = "MLArchitectureRotation:PollIntervalSeconds";
    private const string CK_MinRuns     = "MLArchitectureRotation:MinRunsPerWindow";
    private const string CK_WindowDays  = "MLArchitectureRotation:WindowDays";
    private const string CK_Cooldown    = "MLArchitectureRotation:CooldownMinutes";
    private const string CK_TrainWindow = "MLTraining:TrainingDataWindowDays";
    private const string CK_Blocked     = "MLTraining:BlockedArchitectures";

    // TorchSharp-dependent architectures — excluded from rotation
    private static readonly HashSet<LearnerArchitecture> TorchSharpArchitectures = new()
    {
        LearnerArchitecture.AdaBoost,
        LearnerArchitecture.Svgp,
        LearnerArchitecture.Dann,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLArchitectureRotationWorker> _logger;

    public MLArchitectureRotationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLArchitectureRotationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLArchitectureRotationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 7200;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readDb   = readCtx.GetDbContext();
                var writeDb  = writeCtx.GetDbContext();

                // Read config
                pollSecs    = await CfgInt(readDb, CK_PollSecs, 7200, stoppingToken);
                int minRuns = await CfgInt(readDb, CK_MinRuns, 2, stoppingToken);
                int window  = await CfgInt(readDb, CK_WindowDays, 7, stoppingToken);
                int cooldown = await CfgInt(readDb, CK_Cooldown, 60, stoppingToken);
                int trainWindow = await CfgInt(readDb, CK_TrainWindow, 365, stoppingToken);

                // Parse blocked architectures
                var blocked = await GetBlockedArchitectures(readDb, stoppingToken);

                // Get eligible architectures
                var eligible = Enum.GetValues<LearnerArchitecture>()
                    .Where(a => !TorchSharpArchitectures.Contains(a) && !blocked.Contains(a))
                    .ToList();

                // Get active (Symbol, Timeframe) pairs
                var activeContexts = await readDb.Set<MLModel>()
                    .Where(m => m.IsActive && !m.IsDeleted && m.Symbol != "ALL")
                    .Select(m => new { m.Symbol, m.Timeframe })
                    .Distinct()
                    .ToListAsync(stoppingToken);

                var windowCutoff = DateTime.UtcNow.AddDays(-window);
                var cooldownCutoff = DateTime.UtcNow.AddMinutes(-cooldown);
                var now = DateTime.UtcNow;
                int totalQueued = 0;

                foreach (var ctx2 in activeContexts)
                {
                    // Count recent runs per architecture
                    var recentCounts = await readDb.Set<MLTrainingRun>()
                        .Where(r => r.Symbol == ctx2.Symbol
                                 && r.Timeframe == ctx2.Timeframe
                                 && !r.IsDeleted
                                 && (r.CompletedAt >= windowCutoff
                                     || r.Status == RunStatus.Queued
                                     || r.Status == RunStatus.Running))
                        .GroupBy(r => r.LearnerArchitecture)
                        .Select(g => new { Arch = g.Key, Count = g.Count() })
                        .ToListAsync(stoppingToken);

                    var countMap = recentCounts.ToDictionary(x => x.Arch, x => x.Count);

                    // Also check for TorchSharp failures to skip broken architectures
                    var torchFailedArchs = await readDb.Set<MLTrainingRun>()
                        .Where(r => r.Symbol == ctx2.Symbol
                                 && r.Timeframe == ctx2.Timeframe
                                 && r.Status == RunStatus.Failed
                                 && !r.IsDeleted
                                 && r.ErrorMessage != null
                                 && (r.ErrorMessage.Contains("TorchSharp") || r.ErrorMessage.Contains("libtorch")))
                        .Select(r => r.LearnerArchitecture)
                        .Distinct()
                        .ToListAsync(stoppingToken);

                    var torchFailed = new HashSet<LearnerArchitecture>(torchFailedArchs);

                    foreach (var arch in eligible)
                    {
                        if (torchFailed.Contains(arch)) continue;

                        int runCount = countMap.GetValueOrDefault(arch, 0);
                        if (runCount >= minRuns) continue;

                        // Check cooldown
                        bool recentlyHandled = await readDb.Set<MLTrainingRun>()
                            .AnyAsync(r => r.Symbol == ctx2.Symbol
                                        && r.Timeframe == ctx2.Timeframe
                                        && r.LearnerArchitecture == arch
                                        && !r.IsDeleted
                                        && (r.Status == RunStatus.Queued
                                            || r.Status == RunStatus.Running
                                            || (r.CompletedAt != null && r.CompletedAt > cooldownCutoff)),
                                stoppingToken);

                        if (recentlyHandled) continue;

                        // Queue a rotation run
                        writeDb.Set<MLTrainingRun>().Add(new MLTrainingRun
                        {
                            Symbol              = ctx2.Symbol,
                            Timeframe           = ctx2.Timeframe,
                            TriggerType         = TriggerType.Scheduled,
                            Status              = RunStatus.Queued,
                            FromDate            = now.AddDays(-trainWindow),
                            ToDate              = now,
                            StartedAt           = now,
                            LearnerArchitecture = arch,
                            HyperparamConfigJson = JsonSerializer.Serialize(new
                            {
                                triggeredBy = "MLArchitectureRotationWorker",
                                rotationWindowDays = window,
                                minRunsPerWindow = minRuns,
                            }),
                        });

                        totalQueued++;
                    }
                }

                if (totalQueued > 0)
                {
                    await writeCtx.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation(
                        "MLArchitectureRotationWorker: queued {Count} rotation runs across {Contexts} contexts",
                        totalQueued, activeContexts.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLArchitectureRotationWorker cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }
    }

    private static async Task<int> CfgInt(DbContext db, string key, int defaultValue, CancellationToken ct)
    {
        var row = await db.Set<EngineConfig>()
            .Where(c => c.Key == key && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);

        return int.TryParse(row, out int val) ? val : defaultValue;
    }

    private static async Task<HashSet<LearnerArchitecture>> GetBlockedArchitectures(
        DbContext db, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>()
            .Where(c => c.Key == CK_Blocked && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);

        var result = new HashSet<LearnerArchitecture>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<LearnerArchitecture>(token, ignoreCase: true, out var arch))
                result.Add(arch);
        }
        return result;
    }
}
