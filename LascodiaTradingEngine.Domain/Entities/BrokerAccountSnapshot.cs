using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores periodic broker-reported account state received from EA instances.
/// Used by <c>BrokerPnLReconciliationWorker</c> to detect equity discrepancies
/// between the engine's internal tracking and the broker's actual account state.
/// </summary>
public class BrokerAccountSnapshot : Entity<long>
{
    /// <summary>Trading account this snapshot belongs to.</summary>
    public long TradingAccountId { get; set; }

    /// <summary>EA instance that reported this snapshot.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>Broker-reported account balance (realized P&L only).</summary>
    public decimal Balance { get; set; }

    /// <summary>Broker-reported account equity (balance + floating P&L).</summary>
    public decimal Equity { get; set; }

    /// <summary>Broker-reported margin currently in use.</summary>
    public decimal MarginUsed { get; set; }

    /// <summary>Broker-reported free margin available for new trades.</summary>
    public decimal FreeMargin { get; set; }

    /// <summary>UTC timestamp when the EA captured this snapshot from the broker.</summary>
    public DateTime ReportedAt { get; set; }

    public bool IsDeleted { get; set; }
}
