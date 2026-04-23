using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Handles the critical section that swaps the served CPC encoder for a pair.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ICpcEncoderPromotionService))]
public sealed class CpcEncoderPromotionService : ICpcEncoderPromotionService
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly IActiveCpcEncoderProvider? _activeEncoderProvider;

    public CpcEncoderPromotionService(IActiveCpcEncoderProvider? activeEncoderProvider = null)
    {
        _activeEncoderProvider = activeEncoderProvider;
    }

    public async Task<CpcEncoderPromotionResult> PromoteAsync(
        DbContext writeCtx,
        CpcEncoderPromotionRequest request,
        MLCpcEncoder newEncoder,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writeCtx);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(newEncoder);

        var result = CpcEncoderPromotionResult.Accepted;
        var strategy = writeCtx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async token =>
        {
            await using var tx = await writeCtx.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

            var existingActive = await LoadActiveRowsForPromotionAsync(writeCtx, request, token);

            var currentActive = existingActive.FirstOrDefault();
            if (currentActive is not null &&
                currentActive.EncoderType == newEncoder.EncoderType &&
                currentActive.Id != request.PriorEncoderId &&
                !BeatsPriorLoss(newEncoder.InfoNceLoss, currentActive.InfoNceLoss, request.MinImprovement))
            {
                await tx.RollbackAsync(token);
                result = new CpcEncoderPromotionResult(
                    Promoted: false,
                    Reason: "superseded_by_better_active",
                    CurrentActiveEncoderId: currentActive.Id,
                    CurrentActiveInfoNceLoss: currentActive.InfoNceLoss);
                return;
            }

            foreach (var row in existingActive)
                row.IsActive = false;

            if (existingActive.Count > 0)
                await writeCtx.SaveChangesAsync(token);

            newEncoder.IsActive = true;
            writeCtx.Set<MLCpcEncoder>().Add(newEncoder);
            await writeCtx.SaveChangesAsync(token);
            await tx.CommitAsync(token);
        }, ct);

        if (result.Promoted)
            _activeEncoderProvider?.Invalidate(request.Symbol, request.Timeframe, request.Regime);

        return result;
    }

    private static Task<List<MLCpcEncoder>> LoadActiveRowsForPromotionAsync(
        DbContext writeCtx,
        CpcEncoderPromotionRequest request,
        CancellationToken ct)
    {
        if (string.Equals(writeCtx.Database.ProviderName, PostgresProvider, StringComparison.Ordinal))
            return LoadActiveRowsForPromotionWithUpdateLockAsync(writeCtx, request, ct);

        var query = writeCtx.Set<MLCpcEncoder>()
            .Where(e => e.Symbol == request.Symbol
                     && e.Timeframe == request.Timeframe
                     && e.IsActive
                     && !e.IsDeleted);
        query = request.Regime is null
            ? query.Where(e => e.Regime == null)
            : query.Where(e => e.Regime == request.Regime.Value);

        return query
            .OrderByDescending(e => e.TrainedAt)
            .ThenByDescending(e => e.Id)
            .ToListAsync(ct);
    }

    private static Task<List<MLCpcEncoder>> LoadActiveRowsForPromotionWithUpdateLockAsync(
        DbContext writeCtx,
        CpcEncoderPromotionRequest request,
        CancellationToken ct)
    {
        var timeframe = (int)request.Timeframe;
        var query = request.Regime is null
            ? writeCtx.Set<MLCpcEncoder>().FromSqlInterpolated($"""
                SELECT *
                  FROM "MLCpcEncoder"
                 WHERE "Symbol" = {request.Symbol}
                   AND "Timeframe" = {timeframe}
                   AND "Regime" IS NULL
                   AND "IsActive" = TRUE
                   AND "IsDeleted" = FALSE
                 ORDER BY "TrainedAt" DESC, "Id" DESC
                 FOR UPDATE
                """)
            : writeCtx.Set<MLCpcEncoder>().FromSqlInterpolated($"""
                SELECT *
                  FROM "MLCpcEncoder"
                 WHERE "Symbol" = {request.Symbol}
                   AND "Timeframe" = {timeframe}
                   AND "Regime" = {(int)request.Regime.Value}
                   AND "IsActive" = TRUE
                   AND "IsDeleted" = FALSE
                 ORDER BY "TrainedAt" DESC, "Id" DESC
                 FOR UPDATE
                """);

        return query.ToListAsync(ct);
    }

    private static bool BeatsPriorLoss(double candidateLoss, double priorLoss, double minImprovement)
    {
        if (!double.IsFinite(candidateLoss) || !double.IsFinite(priorLoss))
            return false;

        return candidateLoss < priorLoss * (1.0 - minImprovement);
    }
}
