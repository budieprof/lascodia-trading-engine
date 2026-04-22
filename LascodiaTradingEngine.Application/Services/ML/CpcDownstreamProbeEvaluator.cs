using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Enforces the downstream-proxy gate: the candidate encoder's balanced accuracy on a
/// directional linear probe must clear the absolute floor, and — if a prior encoder is
/// available — beat it by <c>MinDownstreamProbeImprovement</c>. Delegates the actual probe
/// fitting to <see cref="ICpcDownstreamProbeRunner"/>.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ICpcDownstreamProbeEvaluator))]
public sealed class CpcDownstreamProbeEvaluator : ICpcDownstreamProbeEvaluator
{
    private readonly ICpcDownstreamProbeRunner _runner;

    public CpcDownstreamProbeEvaluator(ICpcDownstreamProbeRunner runner)
    {
        _runner = runner;
    }

    public async Task<CpcDownstreamProbeResult> EvaluateAsync(
        DbContext readCtx,
        CpcEncoderGateRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readCtx);
        ArgumentNullException.ThrowIfNull(request);

        var options = request.Options;
        if (!options.EnableDownstreamProbeGate)
            return CpcDownstreamProbeResult.Disabled;

        var candidateProbe = _runner.Evaluate(
            request.Encoder,
            request.TrainingSequences,
            request.ValidationSequences,
            options.PredictionSteps,
            options.MinDownstreamProbeSamples);

        if (!candidateProbe.Evaluable)
            return new CpcDownstreamProbeResult(
                Passed: false,
                Reason: candidateProbe.Reason,
                CandidateBalancedAccuracy: candidateProbe.BalancedAccuracy,
                PriorBalancedAccuracy: null);

        if (candidateProbe.BalancedAccuracy < options.MinDownstreamProbeBalancedAccuracy)
            return new CpcDownstreamProbeResult(
                Passed: false,
                Reason: "downstream_probe_below_floor",
                CandidateBalancedAccuracy: candidateProbe.BalancedAccuracy,
                PriorBalancedAccuracy: null);

        if (request.PriorEncoderId is not { } priorId)
            return new CpcDownstreamProbeResult(
                Passed: true,
                Reason: "downstream_probe_passed",
                CandidateBalancedAccuracy: candidateProbe.BalancedAccuracy,
                PriorBalancedAccuracy: null);

        var priorEncoder = await readCtx.Set<MLCpcEncoder>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == priorId && !e.IsDeleted, ct);
        if (priorEncoder is null || priorEncoder.EncoderType != request.Encoder.EncoderType)
            return new CpcDownstreamProbeResult(
                Passed: true,
                Reason: "downstream_probe_passed_prior_unavailable",
                CandidateBalancedAccuracy: candidateProbe.BalancedAccuracy,
                PriorBalancedAccuracy: null);

        var priorProbe = _runner.Evaluate(
            priorEncoder,
            request.TrainingSequences,
            request.ValidationSequences,
            options.PredictionSteps,
            options.MinDownstreamProbeSamples);

        if (!priorProbe.Evaluable)
            return new CpcDownstreamProbeResult(
                Passed: true,
                Reason: "downstream_probe_passed_prior_unevaluable",
                CandidateBalancedAccuracy: candidateProbe.BalancedAccuracy,
                PriorBalancedAccuracy: priorProbe.BalancedAccuracy);

        if (candidateProbe.BalancedAccuracy + 1e-12 <
            priorProbe.BalancedAccuracy + options.MinDownstreamProbeImprovement)
            return new CpcDownstreamProbeResult(
                Passed: false,
                Reason: "downstream_probe_no_lift",
                CandidateBalancedAccuracy: candidateProbe.BalancedAccuracy,
                PriorBalancedAccuracy: priorProbe.BalancedAccuracy);

        return new CpcDownstreamProbeResult(
            Passed: true,
            Reason: "downstream_probe_passed",
            CandidateBalancedAccuracy: candidateProbe.BalancedAccuracy,
            PriorBalancedAccuracy: priorProbe.BalancedAccuracy);
    }
}
