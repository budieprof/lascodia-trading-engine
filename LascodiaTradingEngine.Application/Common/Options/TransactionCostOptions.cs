using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the TCA (Transaction Cost Analysis) framework.</summary>
public class TransactionCostOptions : ConfigurationOption<TransactionCostOptions>
{
    /// <summary>Polling interval in seconds for the TransactionCostWorker.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Minimum expected net profit in pips after costs for a signal to proceed.</summary>
    public decimal MinNetProfitPips { get; set; } = 1.0m;

    /// <summary>Whether cost-aware signal suppression is enabled.</summary>
    public bool EnableCostAwareSuppression { get; set; } = true;
}
