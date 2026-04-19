using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

/// <summary>
/// Tests for UCB1-weighted template selection introduced in
/// <see cref="StrategyGenerationHelpers.OrderTemplatesForRegimeUcb1"/>. The ordering must
/// (a) always explore untried templates first, (b) favour higher-rate templates when
/// exploration cost is similar, and (c) boost under-explored templates against saturated
/// ones whose confidence interval has collapsed.
/// </summary>
public class Ucb1TemplateOrderingTest
{
    private const MarketRegime AnyRegime = MarketRegime.Trending;

    [Fact]
    public void Ucb1_NoRateData_FallsBackTo_RegimeOrdering()
    {
        // Cold start: no rates / counts → regime heuristic path (existing behaviour).
        var templates = new[] { "{\"Period\":14}", "{\"Period\":21}" };

        var ordered = StrategyGenerationHelpers.OrderTemplatesForRegimeUcb1(
            templates, AnyRegime, templateSurvivalRates: null, templateSampleCounts: null);

        Assert.Equal(templates.Length, ordered.Count);
        Assert.Equal(new HashSet<string>(templates), new HashSet<string>(ordered));
    }

    [Fact]
    public void Ucb1_UntriedTemplates_SortedAhead_Of_TriedOnes()
    {
        // Untried templates get infinite UCB1 score → placed first so the engine explores.
        string tried = "{\"Period\":14}";
        string untried = "{\"Period\":99}";

        var rates  = new Dictionary<string, double>  { [tried] = 0.9 };
        var counts = new Dictionary<string, int>     { [tried] = 10  };

        var ordered = StrategyGenerationHelpers.OrderTemplatesForRegimeUcb1(
            new[] { tried, untried }, AnyRegime, rates, counts);

        Assert.Equal(untried, ordered[0]);
        Assert.Equal(tried,   ordered[1]);
    }

    [Fact]
    public void Ucb1_SaturatedTemplate_Loses_To_UnderExplored_WithSimilarRate()
    {
        // Template A: 48/50 = 0.96 rate, large n → small exploration bonus.
        // Template B: 9/10  = 0.90 rate, small n → larger exploration bonus.
        // At exploration constant √2 the UCB1 for B exceeds A's, so B should rank higher.
        string a = "{\"Period\":14}";
        string b = "{\"Period\":21}";

        var rates  = new Dictionary<string, double> { [a] = 0.96, [b] = 0.90 };
        var counts = new Dictionary<string, int>    { [a] = 50,   [b] = 10   };

        var ordered = StrategyGenerationHelpers.OrderTemplatesForRegimeUcb1(
            new[] { a, b }, AnyRegime, rates, counts);

        Assert.Equal(b, ordered[0]);
        Assert.Equal(a, ordered[1]);
    }

    [Fact]
    public void Ucb1_ZeroExplorationConstant_CollapsesTo_PureRateOrdering()
    {
        // With exploration disabled, the template with the higher rate must win — this is
        // the degenerate equivalence with the legacy survival-rate-only ordering.
        string a = "{\"Period\":14}";
        string b = "{\"Period\":21}";

        var rates  = new Dictionary<string, double> { [a] = 0.60, [b] = 0.95 };
        var counts = new Dictionary<string, int>    { [a] = 5,    [b] = 100  };

        var ordered = StrategyGenerationHelpers.OrderTemplatesForRegimeUcb1(
            new[] { a, b }, AnyRegime, rates, counts, explorationConstant: 0.0);

        Assert.Equal(b, ordered[0]);
        Assert.Equal(a, ordered[1]);
    }

    [Fact]
    public void Ucb1_EqualCounts_EqualRates_PreservesStableOrdering()
    {
        // With identical rates and counts, UCB1 scores tie. Ordering becomes stable (no
        // thrashing between cycles), which is important for reproducible generation.
        string a = "{\"Period\":14}";
        string b = "{\"Period\":21}";

        var rates  = new Dictionary<string, double> { [a] = 0.80, [b] = 0.80 };
        var counts = new Dictionary<string, int>    { [a] = 20,   [b] = 20   };

        var ordered1 = StrategyGenerationHelpers.OrderTemplatesForRegimeUcb1(
            new[] { a, b }, AnyRegime, rates, counts);
        var ordered2 = StrategyGenerationHelpers.OrderTemplatesForRegimeUcb1(
            new[] { a, b }, AnyRegime, rates, counts);

        Assert.Equal(ordered1, ordered2);
    }
}
