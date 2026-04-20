using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.RiskProfiles.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.RiskProfiles;

public class SignalValidatorTest
{
    private readonly SignalValidator _validator;

    public SignalValidatorTest()
    {
        _validator = new SignalValidator(new RiskCheckerOptions(), TimeProvider.System);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RiskProfile CreateDefaultProfile() => new RiskProfile
    {
        Name               = "Test Profile",
        MaxLotSizePerTrade = 1.0m,
    };

    private static CurrencyPair CreateDefaultSymbolSpec() => new CurrencyPair
    {
        Symbol        = "EURUSD",
        BaseCurrency  = "EUR",
        QuoteCurrency = "USD",
        ContractSize  = 100_000m,
        DecimalPlaces = 5,
    };

    private static SignalValidationContext CreateContext(
        RiskProfile? profile = null, CurrencyPair? symbolSpec = null) => new SignalValidationContext
    {
        Profile    = profile ?? CreateDefaultProfile(),
        SymbolSpec = symbolSpec ?? CreateDefaultSymbolSpec(),
    };

    private static TradeSignal CreateValidBuySignal() => new TradeSignal
    {
        Symbol           = "EURUSD",
        Direction        = TradeDirection.Buy,
        EntryPrice       = 1.1000m,
        StopLoss         = 1.0950m,
        TakeProfit       = 1.1100m,
        SuggestedLotSize = 0.5m,
        Confidence       = 0.80m,
        ExpiresAt        = DateTime.UtcNow.AddMinutes(30)
    };

    private static TradeSignal CreateValidSellSignal() => new TradeSignal
    {
        Symbol           = "EURUSD",
        Direction        = TradeDirection.Sell,
        EntryPrice       = 1.1000m,
        StopLoss         = 1.1050m,
        TakeProfit       = 1.0900m,
        SuggestedLotSize = 0.5m,
        Confidence       = 0.80m,
        ExpiresAt        = DateTime.UtcNow.AddMinutes(30)
    };

    // ═══════════════════════════════════════════════════════════════════════════
    //  Pass scenarios
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Should_Pass_Valid_Buy_Signal()
    {
        var result = await _validator.ValidateAsync(CreateValidBuySignal(), CreateContext(), CancellationToken.None);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Should_Pass_Valid_Sell_Signal()
    {
        var result = await _validator.ValidateAsync(CreateValidSellSignal(), CreateContext(), CancellationToken.None);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Should_Pass_When_ML_Disagrees_But_Confidence_Is_High()
    {
        var signal = CreateValidBuySignal();
        signal.MLModelId = 7L;
        signal.MLPredictedDirection = TradeDirection.Sell;
        signal.MLConfidenceScore = 0.90m;
        signal.Confidence = 0.80m; // above default 0.70 threshold

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Should_Pass_When_StopLoss_Not_Required_And_Missing()
    {
        var profile = CreateDefaultProfile();
        profile.RequireStopLoss = false;
        var signal = CreateValidBuySignal();
        signal.StopLoss = null;

        var result = await _validator.ValidateAsync(signal, CreateContext(profile: profile), CancellationToken.None);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Should_Pass_When_StopLoss_Distance_Above_Minimum()
    {
        var profile = CreateDefaultProfile();
        profile.MinStopLossDistancePips = 5m;
        var signal = CreateValidBuySignal();
        signal.EntryPrice = 1.1000m;
        signal.StopLoss = 1.0940m; // 60 pips distance

        var result = await _validator.ValidateAsync(signal, CreateContext(profile: profile), CancellationToken.None);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Should_Pass_When_RiskReward_Above_Minimum()
    {
        var profile = CreateDefaultProfile();
        profile.MinRiskRewardRatio = 1.5m;
        var signal = CreateValidBuySignal();
        signal.EntryPrice = 1.1000m;
        signal.StopLoss = 1.0950m;    // risk = 50 pips
        signal.TakeProfit = 1.1100m;   // reward = 100 pips → 2.0 R:R

        var result = await _validator.ValidateAsync(signal, CreateContext(profile: profile), CancellationToken.None);
        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Reject scenarios
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Should_Reject_Expired_Signal()
    {
        var signal = CreateValidBuySignal();
        signal.ExpiresAt = DateTime.UtcNow.AddMinutes(-5);

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("expired", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_Reject_Zero_LotSize()
    {
        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0m;

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("greater than zero", result.BlockReason);
    }

    [Fact]
    public async Task Should_Reject_Negative_LotSize()
    {
        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = -0.1m;

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("greater than zero", result.BlockReason);
    }

    [Fact]
    public async Task Should_Reject_When_StopLoss_Required_But_Missing()
    {
        var profile = CreateDefaultProfile();
        profile.RequireStopLoss = true;
        var signal = CreateValidBuySignal();
        signal.StopLoss = null;

        var result = await _validator.ValidateAsync(signal, CreateContext(profile: profile), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("stop-loss", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_Reject_Buy_Signal_With_StopLoss_Above_Entry()
    {
        var signal = CreateValidBuySignal();
        signal.StopLoss = 1.1050m; // above entry of 1.1000

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("below entry", result.BlockReason);
    }

    [Fact]
    public async Task Should_Reject_Buy_Signal_With_TakeProfit_Below_Entry()
    {
        var signal = CreateValidBuySignal();
        signal.TakeProfit = 1.0950m; // below entry of 1.1000

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("above entry", result.BlockReason);
    }

    [Fact]
    public async Task Should_Reject_Sell_Signal_With_StopLoss_Below_Entry()
    {
        var signal = CreateValidSellSignal();
        signal.StopLoss = 1.0950m; // below entry of 1.1000

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("above entry", result.BlockReason);
    }

    [Fact]
    public async Task Should_Reject_Sell_Signal_With_TakeProfit_Above_Entry()
    {
        var signal = CreateValidSellSignal();
        signal.TakeProfit = 1.1050m; // above entry of 1.1000

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("below entry", result.BlockReason);
    }

    [Fact]
    public async Task Should_Reject_When_StopLoss_Distance_Below_Minimum()
    {
        var profile = CreateDefaultProfile();
        profile.MinStopLossDistancePips = 10m;
        var signal = CreateValidBuySignal();
        signal.EntryPrice = 1.1000m;
        signal.StopLoss = 1.0995m; // only 5 pips

        var result = await _validator.ValidateAsync(signal, CreateContext(profile: profile), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("pips is below the minimum", result.BlockReason);
    }

    [Fact]
    public async Task Should_Reject_When_RiskReward_Below_Minimum()
    {
        var profile = CreateDefaultProfile();
        profile.MinRiskRewardRatio = 2.0m;
        var signal = CreateValidBuySignal();
        signal.EntryPrice = 1.1000m;
        signal.StopLoss = 1.0950m;    // risk = 50 pips
        signal.TakeProfit = 1.1050m;   // reward = 50 pips → 1.0 R:R

        var result = await _validator.ValidateAsync(signal, CreateContext(profile: profile), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("Risk-reward ratio", result.BlockReason);
    }

    [Fact]
    public async Task Should_Reject_When_ML_Disagrees_And_Confidence_Below_Threshold()
    {
        var signal = CreateValidBuySignal();
        signal.MLModelId = 7L;
        signal.MLPredictedDirection = TradeDirection.Sell;
        signal.MLConfidenceScore = 0.90m;
        signal.Confidence = 0.50m; // below default 0.70 threshold

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("ML model predicts", result.BlockReason);
    }

    [Fact]
    public async Task Should_Pass_When_NoMLModel_Active()
    {
        // Case (a): MLModelId null → no ML configured; agreement gate is skipped entirely.
        var signal = CreateValidBuySignal();
        signal.MLModelId = null;
        signal.MLPredictedDirection = null;
        signal.MLConfidenceScore = null;
        signal.Confidence = 0.50m; // below override floor, but irrelevant when ML isn't active

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Should_Reject_When_MLModel_Active_But_Direction_Missing_And_Low_Confidence()
    {
        // Case (c): the scorer attached a model ID but no direction — previously passed silently.
        // Now fails closed when confidence is below the disagreement-override floor.
        var signal = CreateValidBuySignal();
        signal.MLModelId = 42L;
        signal.MLPredictedDirection = null;
        signal.MLConfidenceScore = null;
        signal.Confidence = 0.50m; // below default 0.70 threshold

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("prediction is incomplete", result.BlockReason);
    }

    [Fact]
    public async Task Should_Pass_When_MLModel_Active_But_Direction_Missing_With_High_Confidence()
    {
        // Case (c) with override: strategy confidence clears the disagreement floor,
        // so a partial-ML state is tolerated. Matches the existing disagreement-override contract.
        var signal = CreateValidBuySignal();
        signal.MLModelId = 42L;
        signal.MLPredictedDirection = null;
        signal.MLConfidenceScore = null;
        signal.Confidence = 0.80m; // above default 0.70

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Should_Reject_When_MLModel_Active_And_Confidence_Missing_And_Low_Confidence()
    {
        // Edge of case (c): direction present but MLConfidenceScore null — also partial.
        var signal = CreateValidBuySignal();
        signal.MLModelId = 42L;
        signal.MLPredictedDirection = TradeDirection.Buy;
        signal.MLConfidenceScore = null;
        signal.Confidence = 0.50m;

        var result = await _validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("prediction is incomplete", result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Clock-skew tolerance on expiry (Fix 7)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExpiresAt_JustPast_WithTolerance_IsStillAccepted()
    {
        // Signal expired 2 s ago. Default ClockSkewToleranceSeconds = 5 → should pass.
        var validator = new SignalValidator(
            new RiskCheckerOptions { ClockSkewToleranceSeconds = 5 },
            TimeProvider.System);

        var signal = CreateValidBuySignal();
        signal.ExpiresAt = DateTime.UtcNow.AddSeconds(-2);

        var result = await validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ExpiresAt_BeyondTolerance_IsRejected()
    {
        var validator = new SignalValidator(
            new RiskCheckerOptions { ClockSkewToleranceSeconds = 5 },
            TimeProvider.System);

        var signal = CreateValidBuySignal();
        signal.ExpiresAt = DateTime.UtcNow.AddSeconds(-30); // Well past tolerance

        var result = await validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("expired", result.BlockReason);
    }

    [Fact]
    public async Task ExpiresAt_ToleranceZero_IsStrictExpiry()
    {
        var validator = new SignalValidator(
            new RiskCheckerOptions { ClockSkewToleranceSeconds = 0 },
            TimeProvider.System);

        var signal = CreateValidBuySignal();
        signal.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);

        var result = await validator.ValidateAsync(signal, CreateContext(), CancellationToken.None);
        Assert.False(result.Passed);
    }
}
