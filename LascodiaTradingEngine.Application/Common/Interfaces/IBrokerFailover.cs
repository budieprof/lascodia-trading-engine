namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface IBrokerFailover
{
    string ActiveBroker { get; }
    Task<bool> SwitchBrokerAsync(string brokerName, CancellationToken ct);
    Task<bool> IsHealthyAsync(CancellationToken ct);
}
