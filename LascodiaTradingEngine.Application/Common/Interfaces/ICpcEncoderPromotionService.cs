using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Atomically rotates active CPC encoder rows and invalidates served-encoder caches.
/// </summary>
public interface ICpcEncoderPromotionService
{
    Task<CpcEncoderPromotionResult> PromoteAsync(
        DbContext writeCtx,
        CpcEncoderPromotionRequest request,
        MLCpcEncoder newEncoder,
        CancellationToken ct);
}

public sealed record CpcEncoderPromotionRequest(
    string Symbol,
    global::LascodiaTradingEngine.Domain.Enums.Timeframe Timeframe,
    global::LascodiaTradingEngine.Domain.Enums.MarketRegime? Regime,
    long? PriorEncoderId,
    double MinImprovement,
    long? ExpectedActiveEncoderId = null);

public sealed record CpcEncoderPromotionResult(
    bool Promoted,
    string? Reason = null,
    long? CurrentActiveEncoderId = null,
    double? CurrentActiveInfoNceLoss = null)
{
    public static readonly CpcEncoderPromotionResult Accepted = new(true);
}
