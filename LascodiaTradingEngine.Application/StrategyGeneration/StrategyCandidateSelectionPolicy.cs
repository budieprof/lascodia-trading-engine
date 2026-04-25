using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Identifies a candidate slot by strategy type, symbol, and timeframe.
/// </summary>
public readonly record struct CandidateCombo(StrategyType StrategyType, string Symbol, Timeframe Timeframe);

/// <summary>
/// Stable identity for a screened candidate, including normalized parameter payload.
/// </summary>
public sealed record CandidateIdentity(
    string CandidateId,
    CandidateCombo Combo,
    string NormalizedParametersJson);

/// <summary>
/// Transparent score breakdown used to explain why one candidate won a combo slot over another.
/// </summary>
public sealed record CandidateSelectionScoreBreakdown
{
    [JsonPropertyName("oosSharpeContribution")]
    public double OosSharpeContribution { get; init; }

    [JsonPropertyName("isSharpeContribution")]
    public double IsSharpeContribution { get; init; }

    [JsonPropertyName("oosProfitFactorContribution")]
    public double OosProfitFactorContribution { get; init; }

    [JsonPropertyName("isProfitFactorContribution")]
    public double IsProfitFactorContribution { get; init; }

    [JsonPropertyName("oosWinRateContribution")]
    public double OosWinRateContribution { get; init; }

    [JsonPropertyName("isWinRateContribution")]
    public double IsWinRateContribution { get; init; }

    [JsonPropertyName("equityCurveContribution")]
    public double EquityCurveContribution { get; init; }

    [JsonPropertyName("walkForwardContribution")]
    public double WalkForwardContribution { get; init; }

    [JsonPropertyName("qualityScoreContribution")]
    public double QualityScoreContribution { get; init; }

    [JsonPropertyName("oosDrawdownPenalty")]
    public double OosDrawdownPenalty { get; init; }

    [JsonPropertyName("isDrawdownPenalty")]
    public double IsDrawdownPenalty { get; init; }

    [JsonPropertyName("totalScore")]
    public double TotalScore { get; init; }
}

/// <summary>
/// Selection output pairing the candidate with its stable identity and ranking score.
/// </summary>
public sealed record CandidateSelectionResult(
    ScreeningOutcome Candidate,
    CandidateIdentity Identity,
    CandidateSelectionScoreBreakdown Score);

/// <summary>
/// Chooses the best surviving candidate per combo after screening has produced multiple
/// variants or templates for the same slot.
/// </summary>
public interface IStrategyCandidateSelectionPolicy
{
    CandidateCombo GetCombo(ScreeningOutcome candidate);
    CandidateIdentity BuildIdentity(ScreeningOutcome candidate);
    CandidateSelectionScoreBreakdown Score(ScreeningOutcome candidate);
    IReadOnlyList<CandidateSelectionResult> SelectBestCandidates(IEnumerable<ScreeningOutcome> passedCandidates);
}

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyCandidateSelectionPolicy))]
/// <summary>
/// Deterministically ranks surviving screening outcomes and keeps only the strongest variant
/// for each strategy-type/symbol/timeframe combo.
/// </summary>
public sealed class StrategyCandidateSelectionPolicy : IStrategyCandidateSelectionPolicy
{
    public CandidateCombo GetCombo(ScreeningOutcome candidate)
        => new(candidate.Strategy.StrategyType, candidate.Strategy.Symbol, candidate.Strategy.Timeframe);

    public CandidateIdentity BuildIdentity(ScreeningOutcome candidate)
    {
        var combo = GetCombo(candidate);
        string normalizedParameters = NormalizeTemplateParameters(candidate.Strategy.ParametersJson);
        string raw = $"{combo.StrategyType}|{combo.Symbol}|{combo.Timeframe}|{normalizedParameters}";
        string candidateId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return new CandidateIdentity(candidateId, combo, normalizedParameters);
    }

    public CandidateSelectionScoreBreakdown Score(ScreeningOutcome candidate)
    {
        var metrics = candidate.Metrics;
        // Favor out-of-sample quality most heavily while still rewarding in-sample robustness
        // and penalizing candidates whose drawdown profile looks fragile.
        double oosSharpeContribution = (double)candidate.OosResult.SharpeRatio * 1_000d;
        double isSharpeContribution = (double)candidate.TrainResult.SharpeRatio * 250d;
        double oosProfitFactorContribution = (double)candidate.OosResult.ProfitFactor * 100d;
        double isProfitFactorContribution = (double)candidate.TrainResult.ProfitFactor * 40d;
        double oosWinRateContribution = (double)candidate.OosResult.WinRate * 20d;
        double isWinRateContribution = (double)candidate.TrainResult.WinRate * 10d;
        double equityCurveContribution = (metrics?.EquityCurveR2 ?? -1.0) * 25d;
        double walkForwardContribution = (metrics?.WalkForwardWindowsPassed ?? 0) * 5d;
        double qualityScore = metrics?.QualityScore > 0
            ? metrics.QualityScore
            : ScreeningQualityScorer.ComputeScore(
                candidate.TrainResult,
                candidate.OosResult,
                metrics?.EquityCurveR2,
                metrics?.WalkForwardWindowsPassed,
                null,
                metrics?.MonteCarloPValue,
                metrics?.ShufflePValue,
                metrics?.MaxTradeTimeConcentration,
                metrics?.MarginalSharpeContribution,
                metrics?.KellySharpeRatio,
                metrics?.FixedLotSharpeRatio);
        double qualityScoreContribution = qualityScore * 5d;
        double oosDrawdownPenalty = (double)candidate.OosResult.MaxDrawdownPct * 100d;
        double isDrawdownPenalty = (double)candidate.TrainResult.MaxDrawdownPct * 50d;

        return new CandidateSelectionScoreBreakdown
        {
            OosSharpeContribution = oosSharpeContribution,
            IsSharpeContribution = isSharpeContribution,
            OosProfitFactorContribution = oosProfitFactorContribution,
            IsProfitFactorContribution = isProfitFactorContribution,
            OosWinRateContribution = oosWinRateContribution,
            IsWinRateContribution = isWinRateContribution,
            EquityCurveContribution = equityCurveContribution,
            WalkForwardContribution = walkForwardContribution,
            QualityScoreContribution = qualityScoreContribution,
            OosDrawdownPenalty = oosDrawdownPenalty,
            IsDrawdownPenalty = isDrawdownPenalty,
            TotalScore = oosSharpeContribution
                + isSharpeContribution
                + oosProfitFactorContribution
                + isProfitFactorContribution
                + oosWinRateContribution
                + isWinRateContribution
                + equityCurveContribution
                + walkForwardContribution
                + qualityScoreContribution
                - oosDrawdownPenalty
                - isDrawdownPenalty,
        };
    }

    /// <summary>
    /// Collapses a set of passing candidates into one best result per combo and returns the
    /// winners ordered by descending total score.
    /// </summary>
    public IReadOnlyList<CandidateSelectionResult> SelectBestCandidates(IEnumerable<ScreeningOutcome> passedCandidates)
        => passedCandidates
            .Select(candidate => new CandidateSelectionResult(candidate, BuildIdentity(candidate), Score(candidate)))
            .GroupBy(result => result.Identity.Combo)
            .Select(group => group
                // Tie-break toward stronger walk-forward evidence, then stable name ordering
                // so repeated runs produce deterministic winner selection.
                .OrderByDescending(result => result.Score.TotalScore)
                .ThenByDescending(result => result.Candidate.Metrics?.WalkForwardWindowsPassed ?? 0)
                .ThenBy(result => result.Candidate.Strategy.Name, StringComparer.Ordinal)
                .First())
            .OrderByDescending(result => result.Score.TotalScore)
            .ThenBy(result => result.Candidate.Strategy.Name, StringComparer.Ordinal)
            .ToList();
}
