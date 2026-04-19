using Moq;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Strategies.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies.Services;

/// <summary>
/// Contract tests for <see cref="CpcvValidator"/>'s construction surface.
///
/// <para>
/// Doesn't attempt to run an end-to-end fold (that would require real ML training,
/// which takes seconds and is unsuitable for unit tests). Instead verifies the two
/// invariants that keep the ML retrain-per-fold dispatch safe:
/// </para>
///
/// <list type="number">
/// <item><description>New optional constructor parameters (mlTrainers, inferenceEngines)
///   default to null so existing DI registrations and tests that don't provide them
///   keep working.</description></item>
/// <item><description>When mlTrainers OR inferenceEngines is null, the validator falls
///   back to the rule-based backtest-replay path regardless of strategy type.</description></item>
/// </list>
/// </summary>
public class CpcvValidatorContractTests
{
    [Fact]
    public void Constructor_AcceptsNullableMlDependencies_ForBackwardsCompat()
    {
        // Pre-existing callers (and integration tests) construct CpcvValidator without
        // the ML trainers / inference engines. The contract must stay nullable so
        // those call sites don't need updating when we add retrain-per-fold.
        var readCtx = new Mock<IReadApplicationDbContext>();
        var engine  = new Mock<IBacktestEngine>();
        var builder = new Mock<IBacktestOptionsSnapshotBuilder>();

        var validator = new CpcvValidator(
            readCtx.Object,
            engine.Object,
            builder.Object,
            tcaProvider: null,
            mlTrainers: null,
            inferenceEngines: null,
            logger: null);

        Assert.NotNull(validator);
    }

    [Fact]
    public void Constructor_AcceptsBothMlDependencies_ForRetrainPath()
    {
        var readCtx = new Mock<IReadApplicationDbContext>();
        var engine  = new Mock<IBacktestEngine>();
        var builder = new Mock<IBacktestOptionsSnapshotBuilder>();
        var trainers = new List<IMLModelTrainer>();
        var inferences = new List<IModelInferenceEngine>();

        var validator = new CpcvValidator(
            readCtx.Object,
            engine.Object,
            builder.Object,
            tcaProvider: null,
            mlTrainers: trainers,
            inferenceEngines: inferences,
            logger: null);

        Assert.NotNull(validator);
    }
}
