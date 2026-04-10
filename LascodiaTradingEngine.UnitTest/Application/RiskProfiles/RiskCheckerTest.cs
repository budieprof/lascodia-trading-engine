using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.RiskProfiles.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.RiskProfiles;

public class RiskCheckerTest
{
    private readonly RiskChecker _riskChecker;

    /// <summary>
    /// Creates a mock <see cref="IReadApplicationDbContext"/> with an empty EngineConfig set.
    /// Used by all RiskChecker constructor calls in tests where drawdown recovery config is not
    /// under test (returns "Normal" mode by default).
    /// </summary>
    private static IReadApplicationDbContext CreateMockReadDb()
        => new MockDbContextBuilder()
            .WithEmptySet<EngineConfig>()
            .BuildReadContext()
            .Object;

    public RiskCheckerTest()
    {
        _riskChecker = new RiskChecker(new RiskCheckerOptions(), new CorrelationGroupOptions(), TimeProvider.System, NullLogger<RiskChecker>.Instance, CreateMockReadDb());
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
        MaxRiskPerTradePct  = 1m,
        MaxSymbolExposurePct = 5m,
        MaxTotalExposurePct  = 50m,
    };

    private static TradingAccount CreateDefaultAccount() => new TradingAccount
    {
        AccountId    = "TEST-001",
        BrokerServer = "TestBroker-Demo",
        BrokerName   = "TestBroker",
        Currency     = "USD",
        Balance      = 100_000m,
        Equity       = 100_000m,
        MarginUsed   = 0m,
        MarginAvailable = 100_000m,
        Leverage     = 100m,
        MarginMode   = MarginMode.Hedging,
        IsActive     = true,
    };

    private static CurrencyPair CreateDefaultSymbolSpec() => new CurrencyPair
    {
        Symbol         = "EURUSD",
        BaseCurrency   = "EUR",
        QuoteCurrency  = "USD",
        ContractSize   = 100_000m,
        DecimalPlaces  = 5,
        MinLotSize     = 0.01m,
        MaxLotSize     = 100m,
        LotStep        = 0.01m,
    };

    private static RiskCheckContext CreateContext(
        RiskProfile? profile = null,
        TradingAccount? account = null,
        IReadOnlyList<Position>? positions = null,
        CurrencyPair? symbolSpec = null,
        int tradesToday = 0,
        bool isInRecoveryMode = false,
        int consecutiveLosses = 0,
        decimal? currentSpread = null,
        decimal dailyStartBalance = 0,
        IReadOnlyDictionary<string, decimal>? portfolioContractSizes = null,
        decimal? quoteToAccountRate = null,
        IReadOnlyDictionary<string, decimal>? portfolioQuoteToAccountRates = null) => new RiskCheckContext
    {
        Profile              = profile ?? CreateDefaultProfile(),
        Account              = account ?? CreateDefaultAccount(),
        OpenPositions        = positions ?? Array.Empty<Position>(),
        SymbolSpec           = symbolSpec ?? CreateDefaultSymbolSpec(),
        TradesToday          = tradesToday,
        IsInRecoveryMode     = isInRecoveryMode,
        ConsecutiveLosses    = consecutiveLosses,
        CurrentSpread        = currentSpread,
        DailyStartBalance    = dailyStartBalance,
        PortfolioContractSizes = portfolioContractSizes,
        QuoteToAccountRate   = quoteToAccountRate,
        PortfolioQuoteToAccountRates = portfolioQuoteToAccountRates,
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
        var result = await _riskChecker.CheckAsync(CreateValidBuySignal(), CreateContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_Valid_Sell_Signal()
    {
        var result = await _riskChecker.CheckAsync(CreateValidSellSignal(), CreateContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — rejection scenarios (Tier 2 account-level checks)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_SymbolSpec_Is_Null()
    {
        var context = new RiskCheckContext
        {
            Profile       = CreateDefaultProfile(),
            Account       = CreateDefaultAccount(),
            OpenPositions = Array.Empty<Position>(),
            SymbolSpec    = null,
        };

        var result = await _riskChecker.CheckAsync(CreateValidBuySignal(), context, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("No symbol specification", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_LotSize_Exceeding_Profile_Max()
    {
        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 2.0m;

        var result = await _riskChecker.CheckAsync(signal, CreateContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("exceeds", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — minimum lot size validation
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_LotSize_Below_Broker_Minimum()
    {
        var spec = CreateDefaultSymbolSpec();
        spec.MinLotSize = 0.01m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.005m;

        var result = await _riskChecker.CheckAsync(signal, CreateContext(symbolSpec: spec), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("broker minimum", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_LotSize_At_Broker_Minimum()
    {
        var spec = CreateDefaultSymbolSpec();
        spec.MinLotSize = 0.01m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.01m;

        var result = await _riskChecker.CheckAsync(signal, CreateContext(symbolSpec: spec), CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — lot step validation
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_LotSize_Not_On_Lot_Step()
    {
        var spec = CreateDefaultSymbolSpec();
        spec.LotStep = 0.01m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.055m; // not a multiple of 0.01

        var result = await _riskChecker.CheckAsync(signal, CreateContext(symbolSpec: spec), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("lot step", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_LotSize_On_Lot_Step()
    {
        var spec = CreateDefaultSymbolSpec();
        spec.LotStep = 0.01m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.05m;

        var result = await _riskChecker.CheckAsync(signal, CreateContext(symbolSpec: spec), CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — broker max lot size
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_LotSize_Exceeds_Broker_Maximum()
    {
        var spec = CreateDefaultSymbolSpec();
        spec.MaxLotSize = 50m;

        var profile = CreateDefaultProfile();
        profile.MaxLotSizePerTrade = 200m; // profile allows it but broker doesn't

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 60m;

        var result = await _riskChecker.CheckAsync(signal, CreateContext(profile: profile, symbolSpec: spec), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("broker maximum", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — minimum equity floor
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Equity_Below_MinFloor()
    {
        var profile = CreateDefaultProfile();
        profile.MinEquityFloor = 1000m;

        var account = CreateDefaultAccount();
        account.Equity = 500m;
        account.Balance = 500m;

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, account: account),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("minimum floor", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_Equity_Above_MinFloor()
    {
        var profile = CreateDefaultProfile();
        profile.MinEquityFloor = 1000m;
        profile.MaxRiskPerTradePct = 100m;
        profile.MaxSymbolExposurePct = 100m;
        profile.MaxTotalExposurePct = 100m;

        var account = CreateDefaultAccount();
        account.Equity = 5000m;
        account.Balance = 5000m;
        account.MarginAvailable = 5000m;

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, account: account),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_Should_Skip_MinFloor_When_Zero()
    {
        var profile = CreateDefaultProfile();
        profile.MinEquityFloor = 0m;
        profile.MaxRiskPerTradePct = 100m;
        profile.MaxSymbolExposurePct = 1000m;
        profile.MaxTotalExposurePct = 1000m;

        var account = CreateDefaultAccount();
        account.Equity = 10m;
        account.Balance = 10m;
        account.MarginAvailable = 100m; // enough margin for the tiny trade
        account.Leverage = 500m;        // high leverage so margin is tiny

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.01m;

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — minimum stop-loss distance
    // ═══════════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — spread validation
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Spread_Exceeds_Maximum()
    {
        var checker = new RiskChecker(new RiskCheckerOptions { MaxSpreadPips = 3m }, new CorrelationGroupOptions(), TimeProvider.System, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        // 5-digit EURUSD: 0.0005 = 5 pips > 3 pip limit
        var result = await checker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(currentSpread: 0.0005m),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("spread", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_Spread_Within_Maximum()
    {
        var checker = new RiskChecker(new RiskCheckerOptions { MaxSpreadPips = 3m }, new CorrelationGroupOptions(), TimeProvider.System, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        // 5-digit EURUSD: 0.0002 = 2 pips < 3 pip limit
        var result = await checker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(currentSpread: 0.0002m),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_Should_Skip_Spread_Check_When_No_Spread_Data()
    {
        var checker = new RiskChecker(new RiskCheckerOptions { MaxSpreadPips = 3m }, new CorrelationGroupOptions(), TimeProvider.System, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        var result = await checker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(currentSpread: null),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — consecutive loss streak gate
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Consecutive_Losses_Reached()
    {
        var profile = CreateDefaultProfile();
        profile.MaxConsecutiveLosses = 5;

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, consecutiveLosses: 5),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Consecutive loss streak", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_Consecutive_Losses_Below_Limit()
    {
        var profile = CreateDefaultProfile();
        profile.MaxConsecutiveLosses = 5;

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, consecutiveLosses: 3),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_Should_Skip_Loss_Streak_When_Zero()
    {
        var profile = CreateDefaultProfile();
        profile.MaxConsecutiveLosses = 0; // disabled

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, consecutiveLosses: 100),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — per-symbol position count
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_PerSymbol_Position_Limit_Reached()
    {
        var profile = CreateDefaultProfile();
        profile.MaxPositionsPerSymbol = 2;

        var positions = new List<Position>
        {
            new() { Symbol = "EURUSD", Direction = PositionDirection.Long, OpenLots = 0.1m, Status = PositionStatus.Open },
            new() { Symbol = "EURUSD", Direction = PositionDirection.Long, OpenLots = 0.1m, Status = PositionStatus.Open },
        };

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, positions: positions),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("per-symbol limit", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_PerSymbol_Limit_Not_Reached()
    {
        var profile = CreateDefaultProfile();
        profile.MaxPositionsPerSymbol = 3;

        var positions = new List<Position>
        {
            new() { Symbol = "EURUSD", Direction = PositionDirection.Long, OpenLots = 0.1m, Status = PositionStatus.Open },
            new() { Symbol = "EURUSD", Direction = PositionDirection.Long, OpenLots = 0.1m, Status = PositionStatus.Open },
        };

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, positions: positions),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — correlated exposure
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Correlated_Positions_Exceeded()
    {
        var profile = CreateDefaultProfile();
        profile.MaxCorrelatedPositions = 2;

        // EURUSD signal — GBPUSD and AUDUSD are in the same default correlation group
        var positions = new List<Position>
        {
            new() { Symbol = "GBPUSD", Direction = PositionDirection.Long, OpenLots = 0.1m, AverageEntryPrice = 1.27m, Status = PositionStatus.Open },
            new() { Symbol = "AUDUSD", Direction = PositionDirection.Long, OpenLots = 0.1m, AverageEntryPrice = 0.65m, Status = PositionStatus.Open },
        };

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, positions: positions),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Correlated position count", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_No_Correlated_Positions()
    {
        var profile = CreateDefaultProfile();
        profile.MaxCorrelatedPositions = 2;

        // EURUSD signal — AUDJPY shares no currencies
        var positions = new List<Position>
        {
            new() { Symbol = "AUDJPY", Direction = PositionDirection.Long, OpenLots = 0.1m, AverageEntryPrice = 95m, Status = PositionStatus.Open },
        };

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, positions: positions),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — daily drawdown gate in CheckAsync
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Daily_Drawdown_Exceeded()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 3m;

        var account = CreateDefaultAccount();
        account.Equity = 97_000m; // 3% down from 100k start

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, account: account, dailyStartBalance: 100_000m),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Daily drawdown", result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — absolute daily loss cap (account-level)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_AbsoluteDailyLoss_Reached()
    {
        var account = CreateDefaultAccount();
        account.Equity = 97_500m; // lost 2500 today
        account.MaxAbsoluteDailyLoss = 2000m;

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(account: account, dailyStartBalance: 100_000m),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("absolute cap", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_AbsoluteDailyLoss_Below_Cap()
    {
        var account = CreateDefaultAccount();
        account.Equity = 99_000m; // lost 1000 today
        account.MaxAbsoluteDailyLoss = 2000m;

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(account: account, dailyStartBalance: 100_000m),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_Should_Skip_AbsoluteDailyLoss_When_Zero()
    {
        var account = CreateDefaultAccount();
        account.Equity = 90_000m; // lost 10000 but cap is disabled
        account.MaxAbsoluteDailyLoss = 0m;

        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 50m; // high enough to not trigger

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, account: account, dailyStartBalance: 100_000m),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — margin & account-level checks
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Insufficient_Margin()
    {
        var account = CreateDefaultAccount();
        account.MarginAvailable = 100m; // very low free margin
        account.Leverage = 50m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 1.0m; // requires ~2200 margin at 50:1

        var result = await _riskChecker.CheckAsync(signal, CreateContext(account: account), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Insufficient margin", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Max_Positions_Reached()
    {
        var profile = CreateDefaultProfile();
        profile.MaxOpenPositions = 2;

        var positions = new List<Position>
        {
            new() { Symbol = "EURUSD", Direction = PositionDirection.Long, OpenLots = 0.1m, Status = PositionStatus.Open },
            new() { Symbol = "GBPUSD", Direction = PositionDirection.Long, OpenLots = 0.1m, Status = PositionStatus.Open },
        };

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, positions: positions),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("position count", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Daily_Trades_Exceeded()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyTrades = 5;

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile, tradesToday: 5),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Daily trade count", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Risk_Per_Trade_Exceeded()
    {
        var account = CreateDefaultAccount();
        account.Equity = 10_000m;
        account.Balance = 10_000m;
        account.MarginAvailable = 10_000m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.5m;
        signal.StopLoss = 1.0950m; // 50 pips risk → 0.5 * 100k * 0.005 * 1.02 ≈ 255 = 2.55% of 10k

        var profile = CreateDefaultProfile();
        profile.MaxRiskPerTradePct = 1m;
        profile.MaxSymbolExposurePct = 100m; // don't trigger symbol exposure
        profile.MaxTotalExposurePct = 100m;  // don't trigger total exposure

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Risk per trade", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_Netting_Multiple_Same_Symbol_As_One_Position()
    {
        var account = CreateDefaultAccount();
        account.MarginMode = MarginMode.Netting;

        var profile = CreateDefaultProfile();
        profile.MaxOpenPositions = 2;

        // Two positions on EURUSD count as one effective position in netting mode
        var positions = new List<Position>
        {
            new() { Symbol = "EURUSD", Direction = PositionDirection.Long, OpenLots = 0.1m, Status = PositionStatus.Open },
            new() { Symbol = "EURUSD", Direction = PositionDirection.Long, OpenLots = 0.2m, Status = PositionStatus.Open },
        };

        var signal = CreateValidBuySignal();
        signal.Symbol = "GBPUSD"; // different symbol = second effective position

        var symbolSpec = CreateDefaultSymbolSpec();
        symbolSpec.Symbol = "GBPUSD";

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account, positions: positions, symbolSpec: symbolSpec),
            CancellationToken.None);

        Assert.True(result.Passed); // 1 effective (EURUSD) + 1 new (GBPUSD) = 2, at limit but not over
    }

    [Fact]
    public async Task CheckAsync_Should_Reject_Margin_Level_Below_Safety_Threshold()
    {
        var account = CreateDefaultAccount();
        account.Equity = 5_000m;
        account.MarginUsed = 4_000m; // current margin level = 125%
        account.MarginAvailable = 1_000m;
        account.Leverage = 100m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.01m; // small trade but margin level already low

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(account: account),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("margin level", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — recovery mode
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_LotSize_Exceeding_Recovery_Adjusted_Max()
    {
        var profile = CreateDefaultProfile();
        profile.MaxLotSizePerTrade = 1.0m;
        profile.RecoveryLotSizeMultiplier = 0.5m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.6m; // exceeds 1.0 * 0.5 = 0.5

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, isInRecoveryMode: true),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("recovery", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_LotSize_Within_Recovery_Adjusted_Max()
    {
        var profile = CreateDefaultProfile();
        profile.MaxLotSizePerTrade = 1.0m;
        profile.RecoveryLotSizeMultiplier = 0.5m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.4m; // within 1.0 * 0.5 = 0.5

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, isInRecoveryMode: true),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_Should_Clamp_Recovery_Multiplier_Above_One()
    {
        var profile = CreateDefaultProfile();
        profile.MaxLotSizePerTrade = 1.0m;
        profile.RecoveryLotSizeMultiplier = 1.5m; // misconfigured — should be clamped to 1.0

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 1.1m; // would pass if multiplier = 1.5, should fail with clamp to 1.0

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, isInRecoveryMode: true),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("recovery", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — total portfolio exposure
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Total_Portfolio_Exposure_Exceeded()
    {
        var account = CreateDefaultAccount();
        account.Equity = 100_000m;
        account.Leverage = 100m;
        account.MarginAvailable = 100_000m;

        var profile = CreateDefaultProfile();
        profile.MaxSymbolExposurePct = 100m; // don't trigger per-symbol
        profile.MaxTotalExposurePct = 5m;    // very tight total cap

        // Existing positions already using significant margin
        var positions = new List<Position>
        {
            new() { Symbol = "GBPUSD", Direction = PositionDirection.Long, OpenLots = 4.0m, AverageEntryPrice = 1.2700m, Status = PositionStatus.Open },
        };

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 1.0m;

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account, positions: positions),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Total portfolio exposure", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Use_PortfolioContractSizes_For_Total_Exposure()
    {
        var account = CreateDefaultAccount();
        account.Equity = 100_000m;
        account.Leverage = 100m;
        account.MarginAvailable = 100_000m;

        var profile = CreateDefaultProfile();
        profile.MaxSymbolExposurePct = 100m;
        profile.MaxTotalExposurePct = 1m; // very tight

        // Gold uses 100 contract size, not 100k
        var positions = new List<Position>
        {
            new() { Symbol = "XAUUSD", Direction = PositionDirection.Long, OpenLots = 1.0m, AverageEntryPrice = 2000m, Status = PositionStatus.Open },
        };

        var contractSizes = new Dictionary<string, decimal> { { "XAUUSD", 100m } };

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.01m;

        // With correct 100 contract size: 1 * 100 * 2000 / 100 = 2000 → 2% (over 1% limit)
        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account, positions: positions, portfolioContractSizes: contractSizes),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Total portfolio exposure", result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — cross-currency margin conversion
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Apply_QuoteToAccountRate_In_Margin_Calculation()
    {
        var account = CreateDefaultAccount();
        account.Equity = 100_000m;
        account.Leverage = 100m;
        account.MarginAvailable = 100_000m;

        var profile = CreateDefaultProfile();
        profile.MaxSymbolExposurePct = 2m; // tight limit

        // EURGBP on a USD account — quote currency is GBP
        var spec = new CurrencyPair
        {
            Symbol = "EURGBP", BaseCurrency = "EUR", QuoteCurrency = "GBP",
            ContractSize = 100_000m, DecimalPlaces = 5,
            MinLotSize = 0.01m, MaxLotSize = 100m, LotStep = 0.01m,
        };

        var signal = CreateValidBuySignal();
        signal.Symbol = "EURGBP";
        signal.EntryPrice = 0.8500m;
        signal.StopLoss = 0.8450m;
        signal.TakeProfit = 0.8600m;
        signal.SuggestedLotSize = 1.0m;

        // Without conversion (rate=1.0): margin = 1 * 100k * 0.85 / 100 = 850 → 0.85% ✓
        // With GBPUSD rate 1.27: margin = 1 * 100k * 0.85 * 1.27 / 100 = 1079.5 → 1.08% ✓
        // With rate 3.0 (hypothetical): margin = 1 * 100k * 0.85 * 3.0 / 100 = 2550 → 2.55% ✗
        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account, symbolSpec: spec, quoteToAccountRate: 3.0m),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Symbol margin exposure", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Default_QuoteToAccountRate_To_One_When_Null()
    {
        var account = CreateDefaultAccount();
        account.Equity = 100_000m;
        account.Leverage = 100m;

        var profile = CreateDefaultProfile();
        profile.MaxSymbolExposurePct = 2m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 1.0m;
        // Without conversion: margin = 1 * 100k * 1.1 / 100 = 1100 → 1.1% (under 2%)

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account, quoteToAccountRate: null),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_Should_Apply_Portfolio_QuoteToAccountRates_In_Total_Exposure()
    {
        var account = CreateDefaultAccount();
        account.Equity = 100_000m;
        account.Leverage = 100m;
        account.MarginAvailable = 100_000m;

        var profile = CreateDefaultProfile();
        profile.MaxSymbolExposurePct = 100m; // don't trigger
        profile.MaxTotalExposurePct = 2m;    // tight total cap

        // EURGBP position on a USD account
        var positions = new List<Position>
        {
            new() { Symbol = "EURGBP", Direction = PositionDirection.Long, OpenLots = 1.0m, AverageEntryPrice = 0.85m, Status = PositionStatus.Open },
        };

        var contractSizes = new Dictionary<string, decimal> { { "EURGBP", 100_000m } };
        // GBPUSD rate 1.27 → margin = 1 * 100k * 0.85 * 1.27 / 100 = 1079.5 → 1.08%
        var portfolioRates = new Dictionary<string, decimal> { { "EURGBP", 1.27m } };

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.5m;
        // New trade margin = 0.5 * 100k * 1.1 * 1.0 / 100 = 550 → 0.55%
        // Total = 1079.5 + 550 = 1629.5 → 1.63% (under 2%)

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account, positions: positions,
                portfolioContractSizes: contractSizes, portfolioQuoteToAccountRates: portfolioRates),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — absolute risk cap
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Reject_When_Absolute_Risk_Cap_Exceeded()
    {
        var profile = CreateDefaultProfile();
        profile.MaxRiskPerTradePct = 100m;          // don't trigger percentage
        profile.MaxSymbolExposurePct = 100m;
        profile.MaxTotalExposurePct = 100m;
        profile.MaxAbsoluteRiskPerTrade = 200m;     // $200 absolute cap

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.5m;
        signal.StopLoss = 1.0950m; // 50 pips → 0.5 * 100k * 0.005 * 1.02 ≈ $255

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("absolute cap", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Pass_When_Absolute_Risk_Cap_Is_Zero()
    {
        var profile = CreateDefaultProfile();
        profile.MaxAbsoluteRiskPerTrade = 0m; // disabled

        var result = await _riskChecker.CheckAsync(
            CreateValidBuySignal(),
            CreateContext(profile: profile),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — direction-aware symbol exposure
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Use_Net_Lots_For_Netting_Accounts()
    {
        var profile = CreateDefaultProfile();
        profile.MaxSymbolExposurePct = 2m; // tight limit

        var account = CreateDefaultAccount();
        account.Equity = 100_000m;
        account.Leverage = 100m;
        account.MarginMode = MarginMode.Netting;

        // 1-lot long + 1-lot short = 0 net lots → adding 0.1 buy = 0.1 net exposure
        var positions = new List<Position>
        {
            new() { Symbol = "EURUSD", Direction = PositionDirection.Long, OpenLots = 1.0m, Status = PositionStatus.Open },
            new() { Symbol = "EURUSD", Direction = PositionDirection.Short, OpenLots = 1.0m, Status = PositionStatus.Open },
        };

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.1m; // net = 0 + 0.1 = 0.1 → 0.11% of 100k

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account, positions: positions),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_Should_Use_Gross_Lots_For_Hedging_Accounts()
    {
        var profile = CreateDefaultProfile();
        profile.MaxSymbolExposurePct = 2m; // tight limit

        var account = CreateDefaultAccount();
        account.Equity = 100_000m;
        account.Leverage = 100m;
        account.MarginMode = MarginMode.Hedging;

        // Hedging: 1-lot long + 1-lot short = 2 lots gross exposure (both consume margin)
        var positions = new List<Position>
        {
            new() { Symbol = "EURUSD", Direction = PositionDirection.Long, OpenLots = 1.0m, Status = PositionStatus.Open },
            new() { Symbol = "EURUSD", Direction = PositionDirection.Short, OpenLots = 1.0m, Status = PositionStatus.Open },
        };

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.1m; // gross = 2.0 + 0.1 = 2.1 lots → 2.1 * 100k * 1.1 / 100 / 100k * 100 = 2.31%

        var result = await _riskChecker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account, positions: positions),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Symbol margin exposure", result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — configurable margin level threshold
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Use_Configurable_Margin_Level_Threshold()
    {
        var checker = new RiskChecker(new RiskCheckerOptions { MinMarginLevelPct = 200m }, new CorrelationGroupOptions(), TimeProvider.System, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        var account = CreateDefaultAccount();
        account.Equity = 10_000m;
        account.MarginUsed = 5_500m; // current margin level = 181%
        account.MarginAvailable = 4_500m;
        account.Leverage = 100m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.01m;

        // With 200% threshold, 181% current level should fail
        var result = await checker.CheckAsync(
            signal,
            CreateContext(account: account),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("200%", result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — broker stop-out floor + credit in margin level
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Use_Broker_StopOut_As_Floor_When_Higher_Than_Config()
    {
        // Engine config: MinMarginLevelPct = 150%, StopOutBufferMultiplier = 2.0
        // Broker stop-out: 100% → floor = 100% × 2.0 = 200% (higher than 150%)
        var checker = new RiskChecker(
            new RiskCheckerOptions { MinMarginLevelPct = 150m, StopOutBufferMultiplier = 2.0m },
            new CorrelationGroupOptions(), TimeProvider.System, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        var account = CreateDefaultAccount();
        account.Equity = 10_000m;
        account.MarginUsed = 5_500m; // current margin level ≈ 181%
        account.MarginAvailable = 4_500m;
        account.Leverage = 100m;
        account.MarginSoStopOut = 100m;
        account.MarginSoMode = "Percent";

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.01m;

        // 181% is above engine's 150% but below broker floor of 200%
        var result = await checker.CheckAsync(signal, CreateContext(account: account), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("broker stop-out", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Ignore_Broker_StopOut_When_Lower_Than_Config()
    {
        // Engine config: MinMarginLevelPct = 150%
        // Broker stop-out: 50% → floor = 50% × 2.0 = 100% (lower than 150%)
        // Effective threshold stays 150%
        var checker = new RiskChecker(
            new RiskCheckerOptions { MinMarginLevelPct = 150m, StopOutBufferMultiplier = 2.0m },
            new CorrelationGroupOptions(), TimeProvider.System, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        var account = CreateDefaultAccount();
        account.Equity = 5_000m;
        account.MarginUsed = 4_000m; // current margin level = 125%
        account.MarginAvailable = 1_000m;
        account.Leverage = 100m;
        account.MarginSoStopOut = 50m;
        account.MarginSoMode = "Percent";

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.01m;

        var result = await checker.CheckAsync(signal, CreateContext(account: account), CancellationToken.None);

        Assert.False(result.Passed);
        // Should use 150% (config), not 100% (broker floor), and NOT mention broker stop-out
        Assert.DoesNotContain("broker stop-out", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Skip_Broker_StopOut_When_Mode_Is_Money()
    {
        // Broker stop-out in Money mode (not Percent) — should be ignored
        var checker = new RiskChecker(
            new RiskCheckerOptions { MinMarginLevelPct = 150m, StopOutBufferMultiplier = 2.0m },
            new CorrelationGroupOptions(), TimeProvider.System, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        var account = CreateDefaultAccount();
        account.Equity = 10_000m;
        account.MarginUsed = 5_500m;
        account.MarginAvailable = 4_500m;
        account.Leverage = 100m;
        account.MarginSoStopOut = 5000m; // $5000 — not a percentage
        account.MarginSoMode = "Money";

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.01m;

        // Should use 150% (config only), not treat 5000 as a percentage
        var result = await checker.CheckAsync(signal, CreateContext(account: account), CancellationToken.None);

        // 181% > 150% → should pass
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_Should_Include_Credit_In_Margin_Level_Calculation()
    {
        var account = CreateDefaultAccount();
        account.Equity = 4_000m;
        account.Credit = 2_000m; // effective equity for margin level = 6_000
        account.MarginUsed = 4_000m;
        account.MarginAvailable = 2_000m;
        account.Leverage = 100m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.01m;

        // Without credit: 4000/4000 = 100% → below 150% → FAIL
        // With credit: 6000/4000 = 150% → at threshold → should PASS (not strictly below)
        // But additional margin from the trade pushes it slightly below → need exact math
        // additionalMargin ≈ 0.01 * 100000 * 1.1 * 1.0 / 100 * 1.02 ≈ 11.22
        // projectedMarginUsed ≈ 4011.22
        // projectedMarginLevel = 6000 / 4011.22 * 100 ≈ 149.58% → below 150% → FAIL

        // So test that WITHOUT credit it would fail with lower margin level message
        var resultNoCredit = await _riskChecker.CheckAsync(
            signal,
            CreateContext(account: new TradingAccount
            {
                AccountId = "TEST", BrokerServer = "Test", BrokerName = "Test",
                Currency = "USD", Balance = 4_000m, Equity = 4_000m, Credit = 0m,
                MarginUsed = 3_500m, MarginAvailable = 500m, Leverage = 100m,
                MarginMode = MarginMode.Hedging, IsActive = true,
            }),
            CancellationToken.None);

        var resultWithCredit = await _riskChecker.CheckAsync(
            signal,
            CreateContext(account: new TradingAccount
            {
                AccountId = "TEST", BrokerServer = "Test", BrokerName = "Test",
                Currency = "USD", Balance = 4_000m, Equity = 4_000m, Credit = 2_000m,
                MarginUsed = 3_500m, MarginAvailable = 2_500m, Leverage = 100m,
                MarginMode = MarginMode.Hedging, IsActive = true,
            }),
            CancellationToken.None);

        // Without credit: 4000/3500 ≈ 114% → below 150% → FAIL
        Assert.False(resultNoCredit.Passed);
        // With credit: (4000+2000)/3500 ≈ 171% → above 150% → PASS
        Assert.True(resultWithCredit.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — TimeProvider testability (weekend gap)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Apply_Weekend_Gap_Multiplier_On_Saturday()
    {
        // Use a fake TimeProvider set to Saturday
        var saturday = new DateTimeOffset(2026, 3, 21, 12, 0, 0, TimeSpan.Zero); // Saturday
        var fakeTime = new FakeTimeProvider(saturday);
        var checker = new RiskChecker(new RiskCheckerOptions(), new CorrelationGroupOptions(), fakeTime, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        var profile = CreateDefaultProfile();
        profile.WeekendGapRiskMultiplier = 2.0m;
        profile.MaxRiskPerTradePct = 1m;
        profile.MaxSymbolExposurePct = 100m;
        profile.MaxTotalExposurePct = 100m;

        var account = CreateDefaultAccount();
        account.Equity = 10_000m;
        account.Balance = 10_000m;
        account.MarginAvailable = 10_000m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.1m;
        signal.StopLoss = 1.0950m;
        // Base risk: 0.1 * 100k * 0.005 * 1.02 = 51 → 0.51% (passes)
        // With 2x weekend gap: 51 * 2 = 102 → 1.02% (fails at 1%)

        var result = await checker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("gap risk multiplier", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Not_Apply_Gap_Multiplier_On_Wednesday()
    {
        var wednesday = new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero); // Wednesday
        var fakeTime = new FakeTimeProvider(wednesday);
        var checker = new RiskChecker(new RiskCheckerOptions(), new CorrelationGroupOptions(), fakeTime, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        var profile = CreateDefaultProfile();
        profile.WeekendGapRiskMultiplier = 2.0m;
        profile.MaxRiskPerTradePct = 1m;
        profile.MaxSymbolExposurePct = 100m;
        profile.MaxTotalExposurePct = 100m;

        var account = CreateDefaultAccount();
        account.Equity = 10_000m;
        account.Balance = 10_000m;
        account.MarginAvailable = 10_000m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.1m;
        signal.StopLoss = 1.0950m;
        // Base risk: 0.1 * 100k * 0.005 * 1.02 = 51 → 0.51% (passes at 1%)

        var result = await checker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckAsync — holiday gap risk
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Apply_Gap_Multiplier_On_Holiday()
    {
        // Christmas Day (Dec 25)
        var christmas = new DateTimeOffset(2026, 12, 25, 10, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(christmas);
        var options = new RiskCheckerOptions { MarketHolidays = ["12-25"] };
        var checker = new RiskChecker(options, new CorrelationGroupOptions(), fakeTime, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        var profile = CreateDefaultProfile();
        profile.WeekendGapRiskMultiplier = 2.0m;
        profile.MaxRiskPerTradePct = 1m;
        profile.MaxSymbolExposurePct = 100m;
        profile.MaxTotalExposurePct = 100m;

        var account = CreateDefaultAccount();
        account.Equity = 10_000m;
        account.Balance = 10_000m;
        account.MarginAvailable = 10_000m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.1m;
        signal.StopLoss = 1.0950m;
        signal.ExpiresAt = christmas.UtcDateTime.AddMinutes(30);

        var result = await checker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("gap risk multiplier", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Should_Apply_Gap_Multiplier_Day_Before_Holiday()
    {
        // Dec 24 at 19:00 UTC (within 4-hour window before 22:00 close, day before Dec 25)
        var christmasEve = new DateTimeOffset(2026, 12, 24, 19, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(christmasEve);
        var options = new RiskCheckerOptions { MarketHolidays = ["12-25"], WeekendGapWindowHours = 4 };
        var checker = new RiskChecker(options, new CorrelationGroupOptions(), fakeTime, NullLogger<RiskChecker>.Instance, CreateMockReadDb());

        var profile = CreateDefaultProfile();
        profile.WeekendGapRiskMultiplier = 2.0m;
        profile.MaxRiskPerTradePct = 1m;
        profile.MaxSymbolExposurePct = 100m;
        profile.MaxTotalExposurePct = 100m;

        var account = CreateDefaultAccount();
        account.Equity = 10_000m;
        account.Balance = 10_000m;
        account.MarginAvailable = 10_000m;

        var signal = CreateValidBuySignal();
        signal.SuggestedLotSize = 0.1m;
        signal.StopLoss = 1.0950m;
        signal.ExpiresAt = christmasEve.UtcDateTime.AddMinutes(30);

        var result = await checker.CheckAsync(
            signal,
            CreateContext(profile: profile, account: account),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("gap risk multiplier", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckDrawdownAsync — passing scenarios
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckDrawdownAsync_Should_Pass_When_Drawdown_Below_Limits()
    {
        var profile = CreateDefaultProfile();
        var result = await _riskChecker.CheckDrawdownAsync(profile, 9800m, 10000m, 10000m, 0m, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Pass_When_PeakBalance_Is_Zero()
    {
        var profile = CreateDefaultProfile();
        var result = await _riskChecker.CheckDrawdownAsync(profile, 5000m, 0m, 5000m, 0m, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Pass_When_PeakBalance_Is_Negative()
    {
        var profile = CreateDefaultProfile();
        var result = await _riskChecker.CheckDrawdownAsync(profile, 5000m, -100m, 5000m, 0m, CancellationToken.None);

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
        var result = await _riskChecker.CheckDrawdownAsync(profile, -100m, 10000m, 10000m, 0m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("negative", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Reject_When_Daily_Drawdown_Exceeded()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 5m;
        profile.MaxTotalDrawdownPct = 20m;

        // Daily start = 10000, current = 9400 → daily dd = 6%, total dd from peak 10000 = 6%
        // Daily limit 5% breached, total limit 20% not breached
        var result = await _riskChecker.CheckDrawdownAsync(profile, 9400m, 10000m, 10000m, 0m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Daily drawdown", result.BlockReason);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Reject_When_Total_Drawdown_Exceeded()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 15m;
        profile.MaxTotalDrawdownPct = 10m;

        // Peak = 10000, current = 8800 → total dd = 12%, daily start = 9000 → daily dd ≈ 2.2%
        var result = await _riskChecker.CheckDrawdownAsync(profile, 8800m, 10000m, 9000m, 0m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Total drawdown", result.BlockReason);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Reject_When_Drawdown_Equals_Daily_Limit()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 5m;
        profile.MaxTotalDrawdownPct = 20m;

        // Daily start = 10000, current = 9500 → exactly 5% daily dd
        var result = await _riskChecker.CheckDrawdownAsync(profile, 9500m, 10000m, 10000m, 0m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Daily drawdown", result.BlockReason);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Report_Total_Drawdown_When_Both_Limits_Breached()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 5m;
        profile.MaxTotalDrawdownPct = 10m;

        // 15% drawdown breaches both limits — total should be reported first
        var result = await _riskChecker.CheckDrawdownAsync(profile, 8500m, 10000m, 10000m, 0m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Total drawdown", result.BlockReason);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Use_DailyStart_Not_Peak_For_Daily_Drawdown()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 5m;
        profile.MaxTotalDrawdownPct = 50m; // high enough to not trigger

        // Peak = 20000 (all-time), daily start = 12000, current = 11500
        // Total dd from peak = 42.5% (under 50%), daily dd from today's start = 4.2% (under 5%)
        var result = await _riskChecker.CheckDrawdownAsync(profile, 11500m, 20000m, 12000m, 0m, CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Trigger_Daily_Independent_Of_Total()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 3m;
        profile.MaxTotalDrawdownPct = 50m;

        // Peak = 20000 (all-time), daily start = 10000, current = 9600
        // Total dd from peak = 52% (breached), but also daily dd = 4% (breached)
        // Total is checked first so it should trigger
        var result = await _riskChecker.CheckDrawdownAsync(profile, 9600m, 20000m, 10000m, 0m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Total drawdown", result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckDrawdownAsync — minimum equity floor
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckDrawdownAsync_Should_Reject_When_Below_MinEquityFloor()
    {
        var profile = CreateDefaultProfile();
        profile.MinEquityFloor = 500m;

        var result = await _riskChecker.CheckDrawdownAsync(profile, 400m, 10000m, 10000m, 0m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("minimum equity floor", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Pass_When_Above_MinEquityFloor()
    {
        var profile = CreateDefaultProfile();
        profile.MinEquityFloor = 500m;

        var result = await _riskChecker.CheckDrawdownAsync(profile, 9800m, 10000m, 10000m, 0m, CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CheckDrawdownAsync — absolute daily loss cap
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckDrawdownAsync_Should_Reject_When_AbsoluteDailyLoss_Reached()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 50m; // high enough to not trigger
        profile.MaxTotalDrawdownPct = 50m;

        // Daily start = 10000, current = 9400 → loss = 600, cap = 500
        var result = await _riskChecker.CheckDrawdownAsync(profile, 9400m, 10000m, 10000m, 500m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("absolute cap", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Reject_When_AbsoluteDailyLoss_Exactly_At_Cap()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 50m;
        profile.MaxTotalDrawdownPct = 50m;

        // Daily start = 10000, current = 9500 → loss = 500, cap = 500 (exactly at limit)
        var result = await _riskChecker.CheckDrawdownAsync(profile, 9500m, 10000m, 10000m, 500m, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("absolute cap", result.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Pass_When_AbsoluteDailyLoss_Below_Cap()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 50m;
        profile.MaxTotalDrawdownPct = 50m;

        // Daily start = 10000, current = 9600 → loss = 400, cap = 500
        var result = await _riskChecker.CheckDrawdownAsync(profile, 9600m, 10000m, 10000m, 500m, CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckDrawdownAsync_Should_Skip_AbsoluteDailyLoss_When_Zero()
    {
        var profile = CreateDefaultProfile();
        profile.MaxDailyDrawdownPct = 50m;
        profile.MaxTotalDrawdownPct = 50m;

        // Loss = 2000 but cap is 0 (disabled)
        var result = await _riskChecker.CheckDrawdownAsync(profile, 8000m, 10000m, 10000m, 0m, CancellationToken.None);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Conversion rate & inverted spread validation
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAsync_Should_Fail_When_QuoteToAccountRate_Is_Zero()
    {
        var context = CreateContext(quoteToAccountRate: 0m);
        var result = await _riskChecker.CheckAsync(CreateValidBuySignal(), context, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Invalid quote-to-account conversion rate", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Fail_When_QuoteToAccountRate_Is_Negative()
    {
        var context = CreateContext(quoteToAccountRate: -1.5m);
        var result = await _riskChecker.CheckAsync(CreateValidBuySignal(), context, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Invalid quote-to-account conversion rate", result.BlockReason);
    }

    [Fact]
    public async Task CheckAsync_Should_Fail_When_Spread_Is_Inverted()
    {
        var checker = new RiskChecker(
            new RiskCheckerOptions { MaxSpreadPips = 5 },
            new CorrelationGroupOptions(),
            TimeProvider.System,
            NullLogger<RiskChecker>.Instance,
            CreateMockReadDb());

        var context = CreateContext(currentSpread: -0.0005m);
        var result = await checker.CheckAsync(CreateValidBuySignal(), context, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Inverted quote detected", result.BlockReason);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FakeTimeProvider — simple test implementation
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
