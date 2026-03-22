using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.RiskProfiles.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.RiskProfiles;

public class RiskCheckerTest
{
    private readonly RiskChecker _riskChecker;

    public RiskCheckerTest()
    {
        _riskChecker = new RiskChecker(new RiskCheckerOptions());
    }

    // ── Helper factories ─────────────────────────────────────────────────────

    private static RiskProfile CreateDefaultProfile() => new RiskProfile
    {
        Name                = "Test Profile",
        MaxLotSizePerTrade  = 1.0m,
        MaxDailyDrawdownPct = 5m,
        MaxTotalDrawdownPct = 10m,
        MaxOpenPositions    = 5,
        MaxDailyTrades      = 10,
        MaxRiskPerTradePct  = 1m
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
    //  CheckAsync — passing scenarios
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Pass_Valid_Buy_Signal()
    {
        var signal  = CreateValidBuySignal();
        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_Valid_Sell_Signal()
    {
        var signal  = CreateValidSellSignal();
        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_ML_Disagrees_But_Confidence_Is_High()
    {
        var signal = CreateValidBuySignal();
        signal.MLPredictedDirection = TradeDirection.Sell;
        signal.MLConfidenceScore   = 0.90m;
        signal.Confidence          = 0.75m; // >= 0.70 threshold

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_ML_Direction_Matches_Signal_Direction()
    {
        var signal = CreateValidBuySignal();
        signal.MLPredictedDirection = TradeDirection.Buy; // agrees
        signal.MLConfidenceScore   = 0.50m;
        signal.Confidence          = 0.30m; // low confidence but ML agrees

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — rejection scenarios
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_Expired_Signal()
    {
        var signal = CreateValidBuySignal();
        signal.ExpiresAt = DateTime.UtcNow.AddMinutes(-5); // expired

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("expired", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_Zero_LotSize()
    {
        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0m;

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("SuggestedLotSize", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_Negative_LotSize()
    {
        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = -0.1m;

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("SuggestedLotSize", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_LotSize_Exceeding_Profile_Max()
    {
        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 2.0m; // exceeds profile max of 1.0

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("exceeds", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_Buy_Signal_With_StopLoss_Above_Entry()
    {
        var signal = CreateValidBuySignal();
        signal.StopLoss = 1.1050m; // above entry of 1.1000

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("stop-loss", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_Buy_Signal_With_TakeProfit_Below_Entry()
    {
        var signal = CreateValidBuySignal();
        signal.TakeProfit = 1.0950m; // below entry of 1.1000

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("take-profit", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_Sell_Signal_With_StopLoss_Below_Entry()
    {
        var signal = CreateValidSellSignal();
        signal.StopLoss = 1.0950m; // below entry of 1.1000

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("stop-loss", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_Sell_Signal_With_TakeProfit_Above_Entry()
    {
        var signal = CreateValidSellSignal();
        signal.TakeProfit = 1.1050m; // above entry of 1.1000

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("take-profit", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_When_ML_Disagrees_And_Confidence_Below_Threshold()
    {
        var signal = CreateValidBuySignal();
        signal.MLPredictedDirection = TradeDirection.Sell; // disagrees
        signal.MLConfidenceScore   = 0.85m;
        signal.Confidence          = 0.60m; // below 0.70 threshold

        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckAsync(signal, profile, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("ML model predicts", result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckDrawdownAsync — passing scenarios
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckDrawdownAsync_Should_Pass_When_Drawdown_Below_Limits()
    {
        var profile = CreateDefaultProfile();
        // drawdownPct = (10000 - 9800) / 10000 * 100 = 2% — below 5% daily and 10% total
        var result = await _riskChecker.CheckDrawdownAsync(profile, 9800m, 10000m, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Pass_When_PeakBalance_Is_Zero()
    {
        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckDrawdownAsync(profile, 5000m, 0m, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Pass_When_PeakBalance_Is_Negative()
    {
        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckDrawdownAsync(profile, 5000m, -100m, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckDrawdownAsync — rejection scenarios
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckDrawdownAsync_Should_Reject_Negative_Balance()
    {
        var profile = CreateDefaultProfile();

        var result = await _riskChecker.CheckDrawdownAsync(profile, -100m, 10000m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("negative", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Reject_When_Daily_Drawdown_Exceeded()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 5m;
        profile.MaxTotalDrawdownPct = 20m; // set high so daily triggers first

        // drawdownPct = (10000 - 9400) / 10000 * 100 = 6% — exceeds 5% daily
        var result = await _riskChecker.CheckDrawdownAsync(profile, 9400m, 10000m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Daily drawdown", result.BlockReason);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Reject_When_Total_Drawdown_Exceeded()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 15m; // set high so total triggers
        profile.MaxTotalDrawdownPct = 10m;

        // drawdownPct = (10000 - 8800) / 10000 * 100 = 12% — exceeds 10% total
        var result = await _riskChecker.CheckDrawdownAsync(profile, 8800m, 10000m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Total drawdown", result.BlockReason);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Reject_When_Drawdown_Equals_Daily_Limit()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 5m;
        profile.MaxTotalDrawdownPct = 20m;

        // drawdownPct = (10000 - 9500) / 10000 * 100 = 5% — equals limit, should reject (>=)
        var result = await _riskChecker.CheckDrawdownAsync(profile, 9500m, 10000m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Daily drawdown", result.BlockReason);
    }
}
