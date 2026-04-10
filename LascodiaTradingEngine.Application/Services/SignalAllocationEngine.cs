using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Distributes approved trade signals across multiple eligible trading accounts
/// using pro-rata equity, equal-risk, or Kelly-optimal allocation methods.
/// Creates one SignalAllocation per account atomically.
/// </summary>
[RegisterService]
public class SignalAllocationEngine : ISignalAllocationEngine
{
    private readonly SignalAllocationOptions _options;
    private readonly ILogger<SignalAllocationEngine> _logger;

    public SignalAllocationEngine(
        SignalAllocationOptions options,
        ILogger<SignalAllocationEngine> logger)
    {
        _options = options;
        _logger  = logger;
    }

    public Task<IReadOnlyList<SignalAllocation>> AllocateAsync(
        TradeSignal signal,
        IReadOnlyList<TradingAccount> eligibleAccounts,
        string allocationMethod,
        CancellationToken cancellationToken)
    {
        if (eligibleAccounts.Count == 0)
            return Task.FromResult<IReadOnlyList<SignalAllocation>>(Array.Empty<SignalAllocation>());

        // Filter accounts below minimum equity
        var qualified = eligibleAccounts
            .Where(a => a.Equity >= _options.MinAccountEquity)
            .ToList();

        if (qualified.Count == 0)
        {
            _logger.LogWarning(
                "SignalAllocation: no accounts meet minimum equity {Min} for signal {Id}",
                _options.MinAccountEquity, signal.Id);
            return Task.FromResult<IReadOnlyList<SignalAllocation>>(Array.Empty<SignalAllocation>());
        }

        var allocations = allocationMethod switch
        {
            "ProRataEquity" => AllocateProRata(signal, qualified),
            "EqualRisk"     => AllocateEqualRisk(signal, qualified),
            "KellyOptimal"  => AllocateKellyOptimal(signal, qualified),
            _               => AllocateProRata(signal, qualified)
        };

        _logger.LogInformation(
            "SignalAllocation: allocated signal {SignalId} across {Count} accounts using {Method}",
            signal.Id, allocations.Count, allocationMethod);

        return Task.FromResult<IReadOnlyList<SignalAllocation>>(allocations);
    }

    private List<SignalAllocation> AllocateProRata(TradeSignal signal, List<TradingAccount> accounts)
    {
        var totalEquity = accounts.Sum(a => a.Equity);
        if (totalEquity <= 0) return new List<SignalAllocation>();

        return accounts.Select(account =>
        {
            var fraction = account.Equity / totalEquity;
            var lots     = Math.Floor(signal.SuggestedLotSize * fraction * 100m) / 100m;

            return new SignalAllocation
            {
                TradeSignalId             = signal.Id,
                TradingAccountId          = account.Id,
                AllocatedLotSize          = lots,
                AllocationMethod          = "ProRataEquity",
                AccountEquityAtAllocation = account.Equity,
                AllocationFraction        = fraction,
                AllocatedAt               = DateTime.UtcNow
            };
        }).Where(a => a.AllocatedLotSize > 0).ToList();
    }

    private List<SignalAllocation> AllocateEqualRisk(TradeSignal signal, List<TradingAccount> accounts)
    {
        // Each account risks the same percentage of its equity
        var riskPct = _options.EqualRiskPercentage;
        if (signal.StopLoss is null || signal.StopLoss == signal.EntryPrice)
            return AllocateProRata(signal, accounts); // Fallback when SL is missing

        var riskPerLot = Math.Abs(signal.EntryPrice - signal.StopLoss.Value);
        if (riskPerLot == 0) return AllocateProRata(signal, accounts);

        return accounts.Select(account =>
        {
            var riskAmount = account.Equity * riskPct;
            var lots       = Math.Floor(riskAmount / riskPerLot * 100m) / 100m;
            lots = Math.Min(lots, signal.SuggestedLotSize); // Don't exceed signal size

            return new SignalAllocation
            {
                TradeSignalId             = signal.Id,
                TradingAccountId          = account.Id,
                AllocatedLotSize          = lots,
                AllocationMethod          = "EqualRisk",
                AccountEquityAtAllocation = account.Equity,
                AllocationFraction        = account.Equity > 0 ? lots * riskPerLot / account.Equity : 0,
                AllocatedAt               = DateTime.UtcNow
            };
        }).Where(a => a.AllocatedLotSize > 0).ToList();
    }

    private List<SignalAllocation> AllocateKellyOptimal(TradeSignal signal, List<TradingAccount> accounts)
    {
        // Use ML Kelly fraction if available, otherwise fall back to pro-rata
        var kellyFraction = signal.MLConfidenceScore.HasValue
            ? Math.Max(0, 2m * signal.MLConfidenceScore.Value - 1m) * 0.5m // Half-Kelly
            : 0m;

        if (kellyFraction <= 0)
            return AllocateProRata(signal, accounts);

        return accounts.Select(account =>
        {
            var lots = Math.Floor(account.Equity * kellyFraction / signal.EntryPrice * 100m) / 100m;
            lots = Math.Min(lots, signal.SuggestedLotSize);

            return new SignalAllocation
            {
                TradeSignalId             = signal.Id,
                TradingAccountId          = account.Id,
                AllocatedLotSize          = lots,
                AllocationMethod          = "KellyOptimal",
                AccountEquityAtAllocation = account.Equity,
                AllocationFraction        = kellyFraction,
                AllocatedAt               = DateTime.UtcNow
            };
        }).Where(a => a.AllocatedLotSize > 0).ToList();
    }
}
